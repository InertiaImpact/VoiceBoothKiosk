using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>Plays and seeks an ordered logical clip through one selected WASAPI endpoint.</summary>
public sealed class WasapiAudioPlaybackService : IAudioPlaybackService
{
    private readonly object sync = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly WasapiAudioDeviceService deviceService;
    private readonly bool ownsDeviceService;
    private WasapiOut? output;
    private MMDevice? outputDevice;
    private LogicalClipWaveStream? clipStream;
    private CancellationTokenSource? progressCancellation;
    private Guid currentClipId;
    private PlaybackTransportState state = PlaybackTransportState.Stopped;
    private TimeSpan lastPosition;
    private bool disposed;

    public WasapiAudioPlaybackService()
        : this(new WasapiAudioDeviceService(), ownsDeviceService: true)
    {
    }

    public WasapiAudioPlaybackService(WasapiAudioDeviceService deviceService)
        : this(deviceService, ownsDeviceService: false)
    {
    }

    private WasapiAudioPlaybackService(WasapiAudioDeviceService deviceService, bool ownsDeviceService)
    {
        this.deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
        this.ownsDeviceService = ownsDeviceService;
    }

    public event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;

    public event EventHandler<VoiceBoothKiosk.Application.PlaybackStateChangedEventArgs>? StateChanged;

    public PlaybackTransportState State
    {
        get
        {
            lock (sync)
            {
                return state;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (sync)
            {
                return clipStream?.CurrentTime ?? lastPosition;
            }
        }
    }

    public async Task PlayAsync(
        AudioPlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OutputDevice.Flow != AudioDeviceFlow.Output)
        {
            throw new ArgumentException("The playback request does not contain an output-device selection.", nameof(request));
        }

        if (request.Segments.Count == 0)
        {
            throw new ArgumentException("Playback requires at least one segment.", nameof(request));
        }

        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            StopCore();
            cancellationToken.ThrowIfCancellationRequested();

            MMDevice? device = null;
            LogicalClipWaveStream? stream = null;
            WasapiOut? newOutput = null;
            try
            {
                device = deviceService.OpenEndpoint(
                    AudioEndpointKind.Output,
                    request.OutputDevice.EndpointId,
                    request.OutputDevice.UseDefault);
                stream = new LogicalClipWaveStream(request.Segments.Select(static segment => segment.WavePath));
                stream.CurrentTime = ClampPosition(request.StartPosition, stream.TotalTime);

                newOutput = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 75);
                newOutput.Init(stream);
                newOutput.PlaybackStopped += OnPlaybackStopped;

                lock (sync)
                {
                    currentClipId = request.ClipId;
                    outputDevice = device;
                    clipStream = stream;
                    output = newOutput;
                    lastPosition = stream.CurrentTime;
                    state = PlaybackTransportState.Playing;
                    progressCancellation = new CancellationTokenSource();
                }

                device = null;
                stream = null;
                newOutput = null;
                output!.Play();
                StartProgressLoop(progressCancellation.Token);
                RaiseStateChanged(request.ClipId, PlaybackTransportState.Playing);
            }
            catch (Exception exception)
            {
                if (newOutput is not null)
                {
                    newOutput.PlaybackStopped -= OnPlaybackStopped;
                    newOutput.Dispose();
                }

                stream?.Dispose();
                device?.Dispose();
                StopCore();
                RaiseStateChanged(request.ClipId, PlaybackTransportState.Faulted, FriendlyPlaybackError(exception));
                throw new AudioDeviceUnavailableException("Playback could not start on the selected output device.", exception);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Guid clipId;
            lock (sync)
            {
                if (state != PlaybackTransportState.Playing || output is null)
                {
                    throw new InvalidOperationException("Playback is not running.");
                }

                output.Pause();
                lastPosition = clipStream?.CurrentTime ?? lastPosition;
                state = PlaybackTransportState.Paused;
                clipId = currentClipId;
            }

            RaiseStateChanged(clipId, PlaybackTransportState.Paused);
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Guid clipId;
            lock (sync)
            {
                if (state != PlaybackTransportState.Paused || output is null)
                {
                    throw new InvalidOperationException("Playback is not paused.");
                }

                output.Play();
                state = PlaybackTransportState.Playing;
                clipId = currentClipId;
            }

            RaiseStateChanged(clipId, PlaybackTransportState.Playing);
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Guid clipId;
            TimeSpan duration;
            lock (sync)
            {
                if (clipStream is null || state is not (PlaybackTransportState.Playing or PlaybackTransportState.Paused))
                {
                    throw new InvalidOperationException("No playable clip is open.");
                }

                clipStream.CurrentTime = ClampPosition(position, clipStream.TotalTime);
                lastPosition = clipStream.CurrentTime;
                clipId = currentClipId;
                duration = clipStream.TotalTime;
            }

            RaiseProgress(clipId, lastPosition, duration);
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Guid clipId;
            var hadPlayback = false;
            lock (sync)
            {
                clipId = currentClipId;
                hadPlayback = output is not null;
            }

            StopCore();
            if (hadPlayback)
            {
                RaiseStateChanged(clipId, PlaybackTransportState.Stopped);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StopCore();
            if (ownsDeviceService)
            {
                deviceService.Dispose();
            }
        }
        finally
        {
            operations.Release();
            operations.Dispose();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (sender is not WasapiOut stoppedOutput)
        {
            return;
        }

        _ = Task.Run(() => CompletePlaybackAsync(stoppedOutput, args.Exception));
    }

    private async Task CompletePlaybackAsync(WasapiOut stoppedOutput, Exception? exception)
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Guid clipId;
            TimeSpan finalPosition;
            TimeSpan duration;
            lock (sync)
            {
                if (!ReferenceEquals(output, stoppedOutput))
                {
                    return;
                }

                clipId = currentClipId;
                duration = clipStream?.TotalTime ?? TimeSpan.Zero;
                finalPosition = clipStream?.CurrentTime ?? lastPosition;
                if (exception is null && clipStream is not null && finalPosition >= duration - TimeSpan.FromMilliseconds(100))
                {
                    finalPosition = duration;
                }

                lastPosition = finalPosition;
            }

            StopCore(callStop: false);
            RaiseProgress(clipId, finalPosition, duration);
            RaiseStateChanged(
                clipId,
                exception is null ? PlaybackTransportState.Stopped : PlaybackTransportState.Faulted,
                exception is null ? null : FriendlyPlaybackError(exception));
        }
        finally
        {
            operations.Release();
        }
    }

    private void StopCore(bool callStop = true)
    {
        WasapiOut? oldOutput;
        LogicalClipWaveStream? oldStream;
        MMDevice? oldDevice;
        CancellationTokenSource? oldProgressCancellation;

        lock (sync)
        {
            if (clipStream is not null)
            {
                lastPosition = clipStream.CurrentTime;
            }

            oldOutput = output;
            oldStream = clipStream;
            oldDevice = outputDevice;
            oldProgressCancellation = progressCancellation;
            output = null;
            clipStream = null;
            outputDevice = null;
            progressCancellation = null;
            state = PlaybackTransportState.Stopped;
        }

        oldProgressCancellation?.Cancel();
        oldProgressCancellation?.Dispose();
        if (oldOutput is not null)
        {
            oldOutput.PlaybackStopped -= OnPlaybackStopped;
            if (callStop)
            {
                try
                {
                    oldOutput.Stop();
                }
                catch
                {
                    // Resource disposal below is still required after a device invalidation.
                }
            }

            oldOutput.Dispose();
        }

        oldStream?.Dispose();
        oldDevice?.Dispose();
    }

    private void StartProgressLoop(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    Guid clipId;
                    TimeSpan position;
                    TimeSpan duration;
                    lock (sync)
                    {
                        if (clipStream is null)
                        {
                            return;
                        }

                        clipId = currentClipId;
                        position = clipStream.CurrentTime;
                        duration = clipStream.TotalTime;
                        lastPosition = position;
                    }

                    RaiseProgress(clipId, position, duration);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void RaiseProgress(Guid clipId, TimeSpan position, TimeSpan duration) =>
        SafeRaise(ProgressChanged, new PlaybackProgressEventArgs
        {
            ClipId = clipId,
            Position = position,
            Duration = duration,
        });

    private void RaiseStateChanged(Guid clipId, PlaybackTransportState newState, string? error = null) =>
        SafeRaise(StateChanged, new VoiceBoothKiosk.Application.PlaybackStateChangedEventArgs
        {
            ClipId = clipId,
            State = newState,
            Error = error,
        });

    private void SafeRaise<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Audio cleanup and device threads do not depend on UI event consumers.
            }
        }
    }

    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return position > duration ? duration : position;
    }

    private static string FriendlyPlaybackError(Exception exception) =>
        exception is AudioDeviceUnavailableException
            ? "The selected output device is unavailable."
            : "Playback stopped because the output device or audio file became unavailable.";
}
