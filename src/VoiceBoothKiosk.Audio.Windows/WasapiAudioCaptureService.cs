using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>
/// Captures one selected WASAPI endpoint at a time and creates finalized 48 kHz, 16-bit,
/// mono WAV segment files. Stop/record-again should use a new instance of this operation and
/// append the returned path to the logical clip.
/// </summary>
public sealed class WasapiAudioCaptureService : IAudioCaptureService, IDisposable
{
    public const int CanonicalSampleRate = 48_000;
    public const int CanonicalBitsPerSample = 16;
    public const int CanonicalChannels = 1;

    private readonly object sync = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly WasapiAudioDeviceService deviceService;
    private readonly bool ownsDeviceService;
    private CaptureOperation? current;
    private AudioCaptureRequest? currentRequest;
    private CompletedApplicationCapture? completedCapture;
    private readonly HashSet<Guid> consumedCompletionIds = [];
    private bool disposed;

    public WasapiAudioCaptureService()
        : this(new WasapiAudioDeviceService(), ownsDeviceService: true)
    {
    }

    public WasapiAudioCaptureService(WasapiAudioDeviceService deviceService)
        : this(deviceService, ownsDeviceService: false)
    {
    }

    private WasapiAudioCaptureService(WasapiAudioDeviceService deviceService, bool ownsDeviceService)
    {
        this.deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
        this.ownsDeviceService = ownsDeviceService;
    }

    public event EventHandler<CaptureMeterEventArgs>? MeterAvailable;

    public event EventHandler<CaptureCompletedEventArgs>? CaptureCompleted;

    public event EventHandler<AudioFaultedEventArgs>? CaptureFaulted;

    public event EventHandler<AudioCaptureProgressEventArgs>? ProgressChanged;

    public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

    public bool IsRecording
    {
        get
        {
            lock (sync)
            {
                return current is not null;
            }
        }
    }

    public Task<CapturedAudioSegment>? CurrentCompletion
    {
        get
        {
            lock (sync)
            {
                return current?.Completion;
            }
        }
    }

    public async Task StartAsync(
        string finalSegmentPath,
        string? endpointId = null,
        bool useDefaultEndpoint = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalSegmentPath);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync)
            {
                if (current is not null || completedCapture is not null)
                {
                    throw new InvalidOperationException(
                        "A recording is already active, still being finalized, or waiting to be acknowledged by Stop.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var device = deviceService.OpenEndpoint(AudioEndpointKind.Input, endpointId, useDefaultEndpoint);
            CaptureOperation? operation = null;
            try
            {
                operation = new CaptureOperation(device, finalSegmentPath, finalSegmentPath + ".partial", OnMeterAvailable);
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    current = operation;
                }

                _ = operation.Completion.ContinueWith(
                    static (completed, state) =>
                    {
                        var (owner, captureOperation) =
                            ((WasapiAudioCaptureService, CaptureOperation))state!;
                        owner.OnOperationFinished(captureOperation, completed);
                    },
                    (this, operation),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                operation.Start();
            }
            catch
            {
                if (operation is null)
                {
                    device.Dispose();
                }
                else
                {
                    operation.AbortStart();
                }

                lock (sync)
                {
                    if (ReferenceEquals(current, operation))
                    {
                        current = null;
                    }
                }

                throw;
            }
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task StartAsync(
        AudioCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCanonicalFormat(request.Format);
        if (request.InputDevice.Flow != AudioDeviceFlow.Input)
        {
            throw new ArgumentException("The capture request does not contain an input-device selection.", nameof(request));
        }

        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync)
            {
                if (current is not null || completedCapture is not null)
                {
                    throw new InvalidOperationException(
                        "A recording is already active, still being finalized, or waiting to be acknowledged by Stop.");
                }
            }

            var device = deviceService.OpenEndpoint(
                AudioEndpointKind.Input,
                request.InputDevice.EndpointId,
                request.InputDevice.UseDefault);
            CaptureOperation? operation = null;
            try
            {
                operation = new CaptureOperation(device, request.FinalWavePath, request.PartialWavePath, OnMeterAvailable);
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    currentRequest = request;
                    current = operation;
                }

                _ = operation.Completion.ContinueWith(
                    static (completed, state) =>
                    {
                        var (owner, captureOperation) =
                            ((WasapiAudioCaptureService, CaptureOperation))state!;
                        owner.OnOperationFinished(captureOperation, completed);
                    },
                    (this, operation),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                operation.Start();
            }
            catch
            {
                if (operation is null)
                {
                    device.Dispose();
                }
                else
                {
                    operation.AbortStart();
                }

                lock (sync)
                {
                    if (ReferenceEquals(current, operation))
                    {
                        current = null;
                        currentRequest = null;
                    }
                }

                throw;
            }
        }
        finally
        {
            operations.Release();
        }
    }

    public async Task<CapturedAudioSegment> StopSegmentAsync(CancellationToken cancellationToken = default)
    {
        CaptureOperation operation;
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync)
            {
                operation = current ?? throw new InvalidOperationException("No recording is active.");
            }

            operation.RequestStop();
        }
        finally
        {
            operations.Release();
        }

        return await operation.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default)
    {
        AudioCaptureRequest? request;
        CompletedApplicationCapture? alreadyCompleted;
        lock (sync)
        {
            alreadyCompleted = completedCapture;
            if (alreadyCompleted is not null)
            {
                completedCapture = null;
                request = alreadyCompleted.Request;
            }
            else
            {
                request = currentRequest;
            }
        }

        if (alreadyCompleted is not null)
        {
            return CreateApplicationResult(alreadyCompleted.Request, alreadyCompleted.Segment);
        }

        if (request is null)
        {
            throw new InvalidOperationException("No application recording is active or awaiting acknowledgement.");
        }
        var segment = await StopSegmentAsync(cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            if (completedCapture?.Request.SegmentId == request.SegmentId)
            {
                // The completion observer ran first and published this exact result.
                completedCapture = null;
            }
            else
            {
                // The awaiting Stop continuation ran first. Prevent the completion observer
                // from publishing a stale result after this call has already consumed it.
                consumedCompletionIds.Add(request.SegmentId);
            }
        }

        return CreateApplicationResult(request, segment);
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        CaptureOperation? operation;
        lock (sync)
        {
            operation = current;
        }

        if (operation is null)
        {
            return;
        }

        operation.RequestStop();
        try
        {
            _ = await operation.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Abort is used for fault cleanup. The partial file is deliberately retained.
        }
    }

    public async ValueTask DisposeAsync()
    {
        CaptureOperation? operation;
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (sync)
            {
                operation = current;
            }

            operation?.RequestStop();
        }
        finally
        {
            operations.Release();
        }

        if (operation is not null)
        {
            try
            {
                await operation.Completion.ConfigureAwait(false);
            }
            catch
            {
                // Disposal is best effort; the partial file remains available for recovery.
            }
        }

        if (ownsDeviceService)
        {
            deviceService.Dispose();
        }

        operations.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void OnMeterAvailable(CaptureMeterEventArgs args)
    {
        try
        {
            MeterAvailable?.Invoke(this, args);
        }
        catch
        {
            // UI/event consumers cannot be allowed to terminate the recording worker.
        }


        AudioCaptureRequest? request;
        lock (sync)
        {
            request = currentRequest;
        }

        if (request is not null)
        {
            SafeRaise(ProgressChanged, new AudioCaptureProgressEventArgs
            {
                SegmentId = request.SegmentId,
                Duration = args.Position,
                RmsLevel = args.Rms,
                PeakLevel = args.Peak,
                NewPeaks = [new WaveformPeak(args.Minimum, args.Maximum)],
            });
        }
    }

    private void OnOperationFinished(CaptureOperation operation, Task<CapturedAudioSegment> completion)
    {
        AudioCaptureRequest? request;
        lock (sync)
        {
            request = currentRequest;
            if (ReferenceEquals(current, operation))
            {
                current = null;
                currentRequest = null;
            }

            if (request is not null && completion.IsCompletedSuccessfully)
            {
                if (!consumedCompletionIds.Remove(request.SegmentId))
                {
                    // An endpoint can disappear after valid PCM has already been captured. The
                    // writer still drains, validates, and atomically finalizes that WAV. Retain
                    // the result so the coordinator's next Stop/End/Dispose receives success
                    // instead of being sent down its recoverable/lost-audio fault path.
                    completedCapture = new CompletedApplicationCapture(request, completion.Result);
                }
            }
        }

        if (completion.IsCompletedSuccessfully)
        {
            var segment = completion.Result;
            SafeRaise(CaptureCompleted, new CaptureCompletedEventArgs(segment));
            // A non-null stop exception is diagnostic when finalization succeeded. Raising the
            // Application Faulted event here would make SessionCoordinator mark and ignore a
            // perfectly valid finalized segment. CaptureCompleted exposes WasInterrupted=true
            // to lower-level diagnostics while StopAsync remains the contract acknowledgement.
        }
        else if (!completion.IsCanceled)
        {
            var exception = completion.Exception?.GetBaseException() ??
                            new IOException("Recording failed for an unknown reason.");
            SafeRaise(
                CaptureFaulted,
                new AudioFaultedEventArgs("Recording", exception, deviceMayBeUnavailable: true));
            if (request is not null)
            {
                SafeRaise(Faulted, new AudioCaptureFaultedEventArgs
                {
                    SegmentId = request.SegmentId,
                    UserMessage = "Recording stopped unexpectedly. Completed audio remains available for recovery.",
                    Exception = exception,
                });
            }
        }
    }

    private static void ValidateCanonicalFormat(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.SampleRate != CanonicalSampleRate ||
            format.Channels != CanonicalChannels ||
            format.BitsPerSample != CanonicalBitsPerSample)
        {
            throw new NotSupportedException("Version 1 records only 48 kHz, 16-bit, mono PCM working audio.");
        }
    }

    private static AudioCaptureResult CreateApplicationResult(
        AudioCaptureRequest request,
        CapturedAudioSegment segment)
    {
        var file = new FileInfo(segment.Path);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "The finalized recording segment disappeared before it could be acknowledged.",
                segment.Path);
        }

        return new AudioCaptureResult
        {
            SegmentId = request.SegmentId,
            Duration = segment.Duration,
            ByteCount = file.Length,
            // The contract intentionally makes this optional. Whole-file hashing after Stop can
            // delay the kiosk for the full duration of a long clip; recovery still validates the
            // finalized RIFF structure, byte count, and duration. A later storage/background
            // integrity job can add a checksum without blocking audio acknowledgement.
            ChecksumSha256 = null,
            FinalWavePath = segment.Path,
        };
    }

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
                // Keep audio cleanup independent from UI subscriber failures.
            }
        }
    }

    private sealed class CaptureOperation
    {
        private static readonly WaveFormat CanonicalFormat =
            new(CanonicalSampleRate, CanonicalBitsPerSample, CanonicalChannels);

        private readonly object sync = new();
        private readonly MMDevice device;
        private readonly WasapiCapture capture;
        private readonly Channel<PooledAudioPacket> packets;
        private readonly QueuedMonoSampleProvider monoSource;
        private readonly WdlResamplingSampleProvider resampler;
        private readonly string finalPath;
        private readonly string partialPath;
        private readonly Action<CaptureMeterEventArgs> meterCallback;
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task<CapturedAudioSegment> writerTask;
        private Exception? fatalPipelineException;
        private Exception? stopException;
        private bool stopRequested;
        private bool acceptingData = true;

        public CaptureOperation(
            MMDevice device,
            string finalSegmentPath,
            string partialSegmentPath,
            Action<CaptureMeterEventArgs> meterCallback)
        {
            this.device = device;
            this.meterCallback = meterCallback;
            finalPath = Path.GetFullPath(finalSegmentPath);
            if (!string.Equals(Path.GetExtension(finalPath), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Recording segment paths must have a .wav extension.", nameof(finalSegmentPath));
            }

            partialPath = Path.GetFullPath(partialSegmentPath);
            if (string.Equals(partialPath, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The partial and final recording paths must be different.", nameof(partialSegmentPath));
            }
            var directory = Path.GetDirectoryName(finalPath) ??
                            throw new ArgumentException("The segment path has no parent directory.", nameof(finalSegmentPath));
            var partialDirectory = Path.GetDirectoryName(partialPath) ??
                                   throw new ArgumentException("The partial segment path has no parent directory.", nameof(partialSegmentPath));
            if (!string.Equals(directory, partialDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The partial and final recording files must be siblings so finalization is atomic.",
                    nameof(partialSegmentPath));
            }

            Directory.CreateDirectory(directory);
            if (File.Exists(finalPath))
            {
                throw new IOException($"The recording segment already exists: {finalPath}");
            }

            if (File.Exists(partialPath))
            {
                throw new IOException($"The partial recording segment already exists and must be recovered first: {partialPath}");
            }

            packets = Channel.CreateBounded<PooledAudioPacket>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

            try
            {
                capture = new WasapiCapture(device, true, 50);
                monoSource = new QueuedMonoSampleProvider(packets.Reader, capture.WaveFormat);
                resampler = new WdlResamplingSampleProvider(monoSource, CanonicalSampleRate);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                writerTask = Task.Run(WriteCanonicalWave);
            }
            catch
            {
                capture?.Dispose();
                device.Dispose();
                throw;
            }
        }

        public Task<CapturedAudioSegment> Completion => writerTask;

        public Exception? StopException
        {
            get
            {
                lock (sync)
                {
                    return stopException;
                }
            }
        }

        public void Start()
        {
            try
            {
                capture.StartRecording();
            }
            catch (Exception exception)
            {
                MarkStopped(exception, fatal: true);
                throw new AudioDeviceUnavailableException("The selected input device could not start recording.", exception);
            }
        }

        public void AbortStart()
        {
            lock (sync)
            {
                fatalPipelineException ??= new OperationCanceledException("Recording did not start.");
            }

            MarkStopped(fatalPipelineException, fatal: true);
        }

        public void RequestStop()
        {
            lock (sync)
            {
                if (stopRequested)
                {
                    return;
                }

                stopRequested = true;
            }

            try
            {
                capture.StopRecording();
            }
            catch (Exception exception)
            {
                MarkStopped(exception, fatal: false);
            }

            _ = EnsureStopCompletesAsync();
        }

        private async Task EnsureStopCompletesAsync()
        {
            try
            {
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                MarkStopped(exception, fatal: false);
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs args)
        {
            if (args.BytesRecorded <= 0 || !Volatile.Read(ref acceptingData))
            {
                return;
            }

            if (args.BytesRecorded % capture.WaveFormat.BlockAlign != 0)
            {
                FailPipeline(new InvalidDataException("The input driver returned a partial audio frame."));
                return;
            }

            var packet = PooledAudioPacket.CopyFrom(args.Buffer, args.BytesRecorded);
            if (packets.Writer.TryWrite(packet))
            {
                return;
            }

            packet.Dispose();
            if (Volatile.Read(ref acceptingData))
            {
                FailPipeline(new AudioBufferOverflowException());
            }
        }

        private void FailPipeline(Exception exception)
        {
            lock (sync)
            {
                fatalPipelineException ??= exception;
            }

            ThreadPool.QueueUserWorkItem(
                static state => ((CaptureOperation)state!).RequestStop(),
                this);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs args) =>
            MarkStopped(args.Exception, fatal: false);

        private void MarkStopped(Exception? exception, bool fatal)
        {
            lock (sync)
            {
                if (!acceptingData)
                {
                    if (exception is not null)
                    {
                        stopException ??= exception;
                        if (fatal)
                        {
                            fatalPipelineException ??= exception;
                        }
                    }

                    return;
                }

                acceptingData = false;
                stopException ??= exception;
                if (fatal && exception is not null)
                {
                    fatalPipelineException ??= exception;
                }
            }

            packets.Writer.TryComplete();
            stopped.TrySetResult();
        }

        private CapturedAudioSegment WriteCanonicalWave()
        {
            long samplesWritten = 0;
            double squareSum = 0;
            float overallPeak = 0;
            var outputBuffer = new float[960]; // 20 ms at 48 kHz

            try
            {
                using (var stream = new FileStream(
                           partialPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.Read,
                           64 * 1024,
                           FileOptions.SequentialScan))
                using (var writer = new WaveFileWriter(stream, CanonicalFormat))
                {
                    while (true)
                    {
                        var read = resampler.Read(outputBuffer, 0, outputBuffer.Length);
                        if (read == 0)
                        {
                            break;
                        }

                        var minimum = 1f;
                        var maximum = -1f;
                        double windowSquareSum = 0;
                        for (var index = 0; index < read; index++)
                        {
                            var sample = Math.Clamp(outputBuffer[index], -1f, 1f);
                            outputBuffer[index] = sample;
                            minimum = Math.Min(minimum, sample);
                            maximum = Math.Max(maximum, sample);
                            var absolute = Math.Abs(sample);
                            overallPeak = Math.Max(overallPeak, absolute);
                            windowSquareSum += sample * sample;
                        }

                        writer.WriteSamples(outputBuffer, 0, read);
                        samplesWritten += read;
                        squareSum += windowSquareSum;

                        meterCallback(new CaptureMeterEventArgs(
                            TimeSpan.FromSeconds((double)samplesWritten / CanonicalSampleRate),
                            minimum,
                            maximum,
                            (float)Math.Sqrt(windowSquareSum / read),
                            Math.Max(Math.Abs(minimum), Math.Abs(maximum)),
                            overallPeak >= 0.999f));
                    }

                    writer.Flush();
                }

                // WaveFileWriter has now rewritten the RIFF lengths. Flush that finalized file
                // through the OS before it is reported as a completed Stop.
                using (var durableStream = new FileStream(
                           partialPath,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.Read,
                           1,
                           FileOptions.WriteThrough))
                {
                    durableStream.Flush(flushToDisk: true);
                }

                Exception? fatal;
                Exception? stoppedBy;
                lock (sync)
                {
                    fatal = fatalPipelineException;
                    stoppedBy = stopException;
                }

                if (fatal is not null)
                {
                    throw new IOException(
                        "Recording stopped because the audio pipeline could not preserve every sample. The partial WAV was retained for recovery.",
                        fatal);
                }

                if (samplesWritten == 0)
                {
                    throw new InvalidDataException("The input device stopped before any audio was captured.");
                }

                var duration = TimeSpan.FromSeconds((double)samplesWritten / CanonicalSampleRate);
                ValidateWave(partialPath, samplesWritten);
                File.Move(partialPath, finalPath, overwrite: false);

                return new CapturedAudioSegment(
                    finalPath,
                    duration,
                    samplesWritten * (CanonicalBitsPerSample / 8),
                    overallPeak,
                    stoppedBy is not null);
            }
            finally
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                monoSource.Dispose();
                device.Dispose();
            }
        }

        private static void ValidateWave(string path, long expectedSamples)
        {
            using var reader = new WaveFileReader(path);
            if (reader.WaveFormat.SampleRate != CanonicalSampleRate ||
                reader.WaveFormat.BitsPerSample != CanonicalBitsPerSample ||
                reader.WaveFormat.Channels != CanonicalChannels ||
                reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
            {
                throw new InvalidDataException("The finalized recording segment is not in the canonical audio format.");
            }

            var expectedBytes = expectedSamples * (CanonicalBitsPerSample / 8);
            if (reader.Length != expectedBytes)
            {
                throw new InvalidDataException(
                    $"The finalized recording segment contains {reader.Length} PCM bytes; {expectedBytes} were expected.");
            }
        }
    }

    private sealed record CompletedApplicationCapture(
        AudioCaptureRequest Request,
        CapturedAudioSegment Segment);
}
