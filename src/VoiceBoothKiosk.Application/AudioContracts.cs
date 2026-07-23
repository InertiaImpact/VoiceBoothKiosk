using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Application;

public enum PlaybackTransportState
{
    Stopped,
    Playing,
    Paused,
    Faulted
}

public readonly record struct WaveformPeak(float Minimum, float Maximum);

public sealed record AudioCaptureRequest
{
    public required Guid SessionId { get; init; }
    public required Guid ClipId { get; init; }
    public required Guid SegmentId { get; init; }
    public required string PartialWavePath { get; init; }
    public required string FinalWavePath { get; init; }
    public string? PeakCachePath { get; init; }
    public required AudioDeviceSelection InputDevice { get; init; }
    public required AudioFormat Format { get; init; }
}

public sealed record AudioCaptureResult
{
    public required Guid SegmentId { get; init; }
    public required TimeSpan Duration { get; init; }
    public required long ByteCount { get; init; }
    public string? ChecksumSha256 { get; init; }
    public required string FinalWavePath { get; init; }
}

public sealed class AudioCaptureProgressEventArgs : EventArgs
{
    public required Guid SegmentId { get; init; }
    public required TimeSpan Duration { get; init; }
    public required float RmsLevel { get; init; }
    public required float PeakLevel { get; init; }
    public IReadOnlyList<WaveformPeak> NewPeaks { get; init; } = [];
}

public sealed class AudioCaptureFaultedEventArgs : EventArgs
{
    public required Guid SegmentId { get; init; }
    public required string UserMessage { get; init; }
    public Exception? Exception { get; init; }
}

public interface IAudioCaptureService : IAsyncDisposable
{
    bool IsRecording { get; }
    event EventHandler<AudioCaptureProgressEventArgs>? ProgressChanged;
    event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;
    Task StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default);
    Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default);
    Task AbortAsync(CancellationToken cancellationToken = default);
}

public sealed record PlaybackSegmentSource
{
    public required Guid SegmentId { get; init; }
    public required string WavePath { get; init; }
    public required TimeSpan Duration { get; init; }
}

public sealed record AudioPlaybackRequest
{
    public required Guid ClipId { get; init; }
    public required IReadOnlyList<PlaybackSegmentSource> Segments { get; init; }
    public required AudioDeviceSelection OutputDevice { get; init; }
    public TimeSpan StartPosition { get; init; }
}

public sealed class PlaybackProgressEventArgs : EventArgs
{
    public required Guid ClipId { get; init; }
    public required TimeSpan Position { get; init; }
    public required TimeSpan Duration { get; init; }
}

public sealed class PlaybackStateChangedEventArgs : EventArgs
{
    public required Guid ClipId { get; init; }
    public required PlaybackTransportState State { get; init; }
    public string? Error { get; init; }
}

public interface IAudioPlaybackService : IAsyncDisposable
{
    PlaybackTransportState State { get; }
    TimeSpan Position { get; }
    event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;
    event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
    Task PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class AudioDevicesChangedEventArgs : EventArgs
{
    public AudioDeviceFlow? Flow { get; init; }
}

public sealed record AudioDeviceTestResult(bool Succeeded, string Message, float PeakLevel = 0);

public interface IAudioDeviceService : IAsyncDisposable
{
    event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;
    Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(AudioDeviceFlow flow, CancellationToken cancellationToken = default);
    Task<AudioDeviceInfo?> ResolveAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default);
    Task<AudioDeviceTestResult> MonitorInputAsync(
        AudioDeviceSelection selection,
        IProgress<float> levelProgress,
        CancellationToken cancellationToken = default);
    Task<AudioDeviceTestResult> TestOutputAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default);
    Task<float?> GetInputGainAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default) =>
        Task.FromResult<float?>(null);
    Task SetInputGainAsync(AudioDeviceSelection selection, float gain, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Input gain control is not available for this audio service."));
    Task<float?> GetOutputVolumeAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default) =>
        Task.FromResult<float?>(null);
    Task SetOutputVolumeAsync(AudioDeviceSelection selection, float volume, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Output volume control is not available for this audio service."));
}

public sealed record WaveformCacheBuildRequest(string WavePath, string PeakCachePath, AudioFormat Format);
public sealed record WaveformLoadRequest(IReadOnlyList<string> PeakCachePaths, int PixelWidth, TimeSpan? VisibleStart = null, TimeSpan? VisibleDuration = null);
public sealed record WaveformData(IReadOnlyList<WaveformPeak> Peaks, TimeSpan Duration, float AbsolutePeak);

public interface IWaveformService
{
    Task BuildCacheAsync(WaveformCacheBuildRequest request, CancellationToken cancellationToken = default);
    Task<WaveformData> LoadAsync(WaveformLoadRequest request, CancellationToken cancellationToken = default);
}
