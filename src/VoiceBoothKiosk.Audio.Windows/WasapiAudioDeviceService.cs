using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>
/// Enumerates Windows Core Audio endpoints using their persistent endpoint IDs and reports
/// endpoint/default-device changes.
/// </summary>
public sealed class WasapiAudioDeviceService : IAudioDeviceService, IDisposable
{
    private readonly object sync = new();
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly NotificationClient notificationClient;
    private bool disposed;

    public WasapiAudioDeviceService()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI audio devices are available only on Windows.");
        }

        notificationClient = new NotificationClient(this);
        var result = enumerator.RegisterEndpointNotificationCallback(notificationClient);
        if (result != 0)
        {
            enumerator.Dispose();
            throw new InvalidOperationException($"Windows rejected the audio endpoint notification registration (HRESULT 0x{result:X8}).");
        }
    }

    public event EventHandler<AudioEndpointChangedEventArgs>? EndpointChanged;

    public event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;

    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(
        AudioEndpointKind kind,
        bool includeInactive = false)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var dataFlow = ToDataFlow(kind);
            var state = includeInactive ? DeviceState.All : DeviceState.Active;
            var defaultId = TryGetDefaultEndpointId(dataFlow);
            var devices = enumerator.EnumerateAudioEndPoints(dataFlow, state);

            var result = new List<AudioEndpointInfo>(devices.Count);
            foreach (var device in devices)
            {
                using (device)
                {
                    result.Add(CreateInfo(device, kind, defaultId));
                }
            }

            return result
                .OrderByDescending(static endpoint => endpoint.IsDefault)
                .ThenBy(static endpoint => endpoint.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static endpoint => endpoint.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public AudioEndpointInfo? GetDefaultEndpoint(AudioEndpointKind kind)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var flow = ToDataFlow(kind);
            if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                return null;
            }

            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return CreateInfo(device, kind, device.ID);
        }
    }

    public Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(
        AudioDeviceFlow flow,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = GetEndpoints(ToEndpointKind(flow));
        IReadOnlyList<AudioDeviceInfo> result = endpoints.Select(static endpoint => new AudioDeviceInfo
        {
            EndpointId = endpoint.Id,
            FriendlyName = endpoint.FriendlyName,
            Flow = endpoint.Kind == AudioEndpointKind.Input ? AudioDeviceFlow.Input : AudioDeviceFlow.Output,
            IsDefault = endpoint.IsDefault,
            IsAvailable = endpoint.State == AudioEndpointState.Active,
            // WASAPI shared mode performs the endpoint conversion. The application records in
            // this one supported canonical working format regardless of the hardware mix format.
            SupportedFormats = [AudioFormat.CanonicalMono],
        }).ToArray();
        return Task.FromResult(result);
    }

    public Task<AudioDeviceInfo?> ResolveAsync(
        AudioDeviceSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        var kind = ToEndpointKind(selection.Flow);
        AudioEndpointInfo? endpoint;
        if (selection.UseDefault)
        {
            endpoint = GetDefaultEndpoint(kind);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(selection.EndpointId))
            {
                return Task.FromResult<AudioDeviceInfo?>(null);
            }

            endpoint = GetEndpoints(kind, includeInactive: true)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Id,
                    selection.EndpointId,
                    StringComparison.Ordinal));
        }

        if (endpoint is null)
        {
            return Task.FromResult<AudioDeviceInfo?>(null);
        }

        return Task.FromResult<AudioDeviceInfo?>(new AudioDeviceInfo
        {
            EndpointId = endpoint.Id,
            FriendlyName = endpoint.FriendlyName,
            Flow = selection.Flow,
            IsDefault = endpoint.IsDefault,
            IsAvailable = endpoint.State == AudioEndpointState.Active,
            SupportedFormats = endpoint.State == AudioEndpointState.Active
                ? [AudioFormat.CanonicalMono]
                : [],
        });
    }

    public Task<AudioDeviceTestResult> MonitorInputAsync(
        AudioDeviceSelection selection,
        IProgress<float> levelProgress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(levelProgress);
        if (selection.Flow != AudioDeviceFlow.Input)
        {
            throw new ArgumentException("The selection is not an input device.", nameof(selection));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new AudioDeviceTestResult(true, "Microphone test stopped."));
        }

        // NAudio captures the current SynchronizationContext when some WASAPI objects are
        // created. Keep the full test lifecycle off the WPF dispatcher so its stopped event
        // can still complete while the UI thread is synchronously shutting the app down.
        return Task.Run(
            () => MonitorInputCoreAsync(selection, levelProgress, cancellationToken),
            CancellationToken.None);
    }

    private async Task<AudioDeviceTestResult> MonitorInputCoreAsync(
        AudioDeviceSelection selection,
        IProgress<float> levelProgress,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new AudioDeviceTestResult(true, "Microphone test stopped.");
        }

        using var device = OpenEndpoint(
            AudioEndpointKind.Input,
            selection.EndpointId,
            selection.UseDefault);
        using var capture = new WasapiCapture(device, true, 50);
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.RecordingStopped += (_, args) => stopped.TrySetResult(args.Exception);
        var peakMeter = new AudioSamplePeakMeter(capture.WaveFormat);
        var peakSync = new object();
        var highestPeak = 0f;
        capture.DataAvailable += (_, args) =>
        {
            var peak = peakMeter.Calculate(args.Buffer, args.BytesRecorded);
            lock (peakSync)
            {
                highestPeak = Math.Max(highestPeak, peak);
            }
            levelProgress.Report(peak);
        };

        float ReadHighestPeak()
        {
            lock (peakSync)
            {
                return highestPeak;
            }
        }

        try
        {
            capture.StartRecording();
            levelProgress.Report(0);
            try
            {
                var unexpectedError = await stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    capture.StopRecording();
                }
                catch
                {
                }

                return new AudioDeviceTestResult(
                    false,
                    unexpectedError is null
                        ? "The microphone stopped unexpectedly. Select Start Mic Test to try again."
                        : $"The microphone stopped unexpectedly: {unexpectedError.Message}",
                    ReadHighestPeak());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    capture.StopRecording();
                }
                catch
                {
                }

                var stopError = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)
                    .ConfigureAwait(false);
                return stopError is null
                    ? new AudioDeviceTestResult(true, "Microphone test stopped.", ReadHighestPeak())
                    : new AudioDeviceTestResult(false, $"The microphone test stopped with an error: {stopError.Message}", ReadHighestPeak());
            }
        }
        catch (Exception exception)
        {
            return new AudioDeviceTestResult(false, $"The microphone could not be tested: {exception.Message}");
        }
    }

    public Task<AudioDeviceTestResult> TestOutputAsync(
        AudioDeviceSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Flow != AudioDeviceFlow.Output)
        {
            throw new ArgumentException("The selection is not an output device.", nameof(selection));
        }

        return Task.Run(
            () => TestOutputCoreAsync(selection, cancellationToken),
            CancellationToken.None);
    }

    private async Task<AudioDeviceTestResult> TestOutputCoreAsync(
        AudioDeviceSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var device = OpenEndpoint(
            AudioEndpointKind.Output,
            selection.EndpointId,
            selection.UseDefault);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 75);
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) => stopped.TrySetResult(args.Exception);

        try
        {
            var firstNote = new SignalGenerator(48_000, 2)
            {
                Frequency = 660,
                Gain = 0.12,
                Type = SignalGeneratorType.Sin,
            }.Take(TimeSpan.FromMilliseconds(800));
            var pause = new SignalGenerator(48_000, 2)
            {
                Frequency = 660,
                Gain = 0,
                Type = SignalGeneratorType.Sin,
            }.Take(TimeSpan.FromMilliseconds(180));
            var secondNote = new SignalGenerator(48_000, 2)
            {
                Frequency = 880,
                Gain = 0.12,
                Type = SignalGeneratorType.Sin,
            }.Take(TimeSpan.FromMilliseconds(800));
            var tone = new ConcatenatingSampleProvider([firstNote, pause, secondNote]);
            output.Init(tone.ToWaveProvider16());
            output.Play();

            var error = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
            return error is null
                ? new AudioDeviceTestResult(true, "The two-note speaker test played successfully.")
                : new AudioDeviceTestResult(false, "The output device stopped unexpectedly.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop on this thread-pool continuation rather than in a token callback. Some
            // audio drivers do real work in Stop(), so it must not run on the WPF dispatcher.
            try
            {
                output.Stop();
            }
            catch
            {
            }

            try
            {
                _ = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
        catch (Exception exception)
        {
            return new AudioDeviceTestResult(false, $"The output device could not be tested: {exception.Message}");
        }
    }

    public Task<float?> GetOutputVolumeAsync(
        AudioDeviceSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Flow != AudioDeviceFlow.Output)
        {
            throw new ArgumentException("The selection is not an output device.", nameof(selection));
        }

        using var device = OpenEndpoint(AudioEndpointKind.Output, selection.EndpointId, selection.UseDefault);
        return Task.FromResult<float?>(device.AudioEndpointVolume.MasterVolumeLevelScalar);
    }

    public Task<float?> GetInputGainAsync(
        AudioDeviceSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Flow != AudioDeviceFlow.Input)
        {
            throw new ArgumentException("The selection is not an input device.", nameof(selection));
        }

        try
        {
            using var device = OpenEndpoint(AudioEndpointKind.Input, selection.EndpointId, selection.UseDefault);
            return Task.FromResult<float?>(device.AudioEndpointVolume.MasterVolumeLevelScalar);
        }
        catch (Exception exception) when (exception is COMException or NotSupportedException)
        {
            return Task.FromResult<float?>(null);
        }
    }

    public Task SetInputGainAsync(
        AudioDeviceSelection selection,
        float gain,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Flow != AudioDeviceFlow.Input)
        {
            throw new ArgumentException("The selection is not an input device.", nameof(selection));
        }

        using var device = OpenEndpoint(AudioEndpointKind.Input, selection.EndpointId, selection.UseDefault);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(gain, 0, 1);
        return Task.CompletedTask;
    }

    public Task SetOutputVolumeAsync(
        AudioDeviceSelection selection,
        float volume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Flow != AudioDeviceFlow.Output)
        {
            throw new ArgumentException("The selection is not an output device.", nameof(selection));
        }

        using var device = OpenEndpoint(AudioEndpointKind.Output, selection.EndpointId, selection.UseDefault);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 1);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves a persisted endpoint selection. Exact endpoint IDs are required; a missing
    /// endpoint is never silently replaced with another microphone or speaker.
    /// </summary>
    internal MMDevice OpenEndpoint(AudioEndpointKind kind, string? endpointId, bool useDefault)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            MMDevice? device = null;
            try
            {
                var flow = ToDataFlow(kind);
                if (useDefault)
                {
                    if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
                    {
                        throw new AudioDeviceUnavailableException($"Windows has no active default {kind.ToString().ToLowerInvariant()} audio device.");
                    }

                    device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(endpointId))
                    {
                        throw new ArgumentException("A Windows endpoint ID is required when useDefault is false.", nameof(endpointId));
                    }

                    device = enumerator.GetDevice(endpointId);
                }

                if (device.DataFlow != flow || device.State != DeviceState.Active)
                {
                    throw new AudioDeviceUnavailableException(
                        $"The selected {kind.ToString().ToLowerInvariant()} device is not active.");
                }

                return device;
            }
            catch (AudioDeviceUnavailableException)
            {
                device?.Dispose();
                throw;
            }
            catch (Exception exception)
            {
                device?.Dispose();
                throw new AudioDeviceUnavailableException(
                    $"The selected {kind.ToString().ToLowerInvariant()} device is unavailable.",
                    exception);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            _ = enumerator.UnregisterEndpointNotificationCallback(notificationClient);
            enumerator.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static DataFlow ToDataFlow(AudioEndpointKind kind) =>
        kind == AudioEndpointKind.Input ? DataFlow.Capture : DataFlow.Render;

    private static AudioEndpointKind ToEndpointKind(AudioDeviceFlow flow) =>
        flow == AudioDeviceFlow.Input ? AudioEndpointKind.Input : AudioEndpointKind.Output;

    private string? TryGetDefaultEndpointId(DataFlow flow)
    {
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
        {
            return null;
        }

        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return defaultDevice.ID;
    }

    private static AudioEndpointInfo CreateInfo(
        MMDevice device,
        AudioEndpointKind kind,
        string? defaultId)
    {
        int? sampleRate = null;
        int? channels = null;
        int? bits = null;

        if (device.State == DeviceState.Active)
        {
            try
            {
                var format = device.AudioClient.MixFormat;
                sampleRate = format.SampleRate;
                channels = format.Channels;
                bits = format.BitsPerSample;
            }
            catch
            {
                // Some drivers expose an active endpoint but reject mix-format inspection.
                // Enumeration remains useful even when the optional format fields are absent.
            }
        }

        string friendlyName;
        try
        {
            friendlyName = device.FriendlyName;
        }
        catch
        {
            friendlyName = "Audio device";
        }

        return new AudioEndpointInfo(
            device.ID,
            friendlyName,
            kind,
            ToEndpointState(device.State),
            string.Equals(device.ID, defaultId, StringComparison.Ordinal),
            sampleRate,
            channels,
            bits);
    }

    private static AudioEndpointState ToEndpointState(DeviceState state) => state switch
    {
        DeviceState.Active => AudioEndpointState.Active,
        DeviceState.Disabled => AudioEndpointState.Disabled,
        DeviceState.NotPresent => AudioEndpointState.NotPresent,
        DeviceState.Unplugged => AudioEndpointState.Unplugged,
        _ => AudioEndpointState.Unknown,
    };

    private void QueueNotification(AudioEndpointChangedEventArgs args)
    {
        var handlers = EndpointChanged;
        if (disposed)
        {
            return;
        }

        if (handlers is not null)
        {
            ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    var (sender, eventHandlers, eventArgs) =
                        ((WasapiAudioDeviceService, EventHandler<AudioEndpointChangedEventArgs>, AudioEndpointChangedEventArgs))state!;

                    foreach (EventHandler<AudioEndpointChangedEventArgs> handler in eventHandlers.GetInvocationList())
                    {
                        try
                        {
                            handler(sender, eventArgs);
                        }
                        catch
                        {
                            // A consumer must not be able to throw back into a Windows COM notification callback.
                        }
                    }
                },
                (this, handlers, args));
        }

        // Volume sliders and several drivers emit property notifications continuously.
        // Those do not change device availability and must not tear down an active test.
        var applicationHandlers = args.ChangeKind == AudioEndpointChangeKind.PropertiesChanged
            ? null
            : DevicesChanged;
        if (applicationHandlers is not null)
        {
            var flow = args.EndpointKind switch
            {
                AudioEndpointKind.Input => AudioDeviceFlow.Input,
                AudioEndpointKind.Output => AudioDeviceFlow.Output,
                _ => (AudioDeviceFlow?)null,
            };
            ThreadPool.QueueUserWorkItem(
                static state =>
                {
                    var (sender, eventHandlers, eventArgs) =
                        ((WasapiAudioDeviceService, EventHandler<AudioDevicesChangedEventArgs>, AudioDevicesChangedEventArgs))state!;
                    foreach (EventHandler<AudioDevicesChangedEventArgs> handler in eventHandlers.GetInvocationList())
                    {
                        try
                        {
                            handler(sender, eventArgs);
                        }
                        catch
                        {
                        }
                    }
                },
                (this, applicationHandlers, new AudioDevicesChangedEventArgs { Flow = flow }));
        }
    }

    private sealed class NotificationClient(WasapiAudioDeviceService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            owner.QueueNotification(new(AudioEndpointChangeKind.StateChanged, deviceId));

        public void OnDeviceAdded(string pwstrDeviceId) =>
            owner.QueueNotification(new(AudioEndpointChangeKind.Added, pwstrDeviceId));

        public void OnDeviceRemoved(string deviceId) =>
            owner.QueueNotification(new(AudioEndpointChangeKind.Removed, deviceId));

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role != Role.Multimedia || flow == DataFlow.All)
            {
                return;
            }

            owner.QueueNotification(new(
                AudioEndpointChangeKind.DefaultChanged,
                defaultDeviceId,
                flow == DataFlow.Capture ? AudioEndpointKind.Input : AudioEndpointKind.Output));
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
            owner.QueueNotification(new(AudioEndpointChangeKind.PropertiesChanged, pwstrDeviceId));
    }
}
