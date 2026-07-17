namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>The direction of a Windows audio endpoint.</summary>
public enum AudioEndpointKind
{
    Input,
    Output,
}

/// <summary>The current Windows endpoint state.</summary>
public enum AudioEndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
    Unknown,
}

/// <summary>A Windows Core Audio endpoint. <see cref="Id"/> is the stable endpoint ID.</summary>
public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    AudioEndpointKind Kind,
    AudioEndpointState State,
    bool IsDefault,
    int? MixSampleRate,
    int? MixChannels,
    int? MixBitsPerSample);

public enum AudioEndpointChangeKind
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged,
    PropertiesChanged,
}

public sealed class AudioEndpointChangedEventArgs(
    AudioEndpointChangeKind changeKind,
    string? endpointId,
    AudioEndpointKind? endpointKind = null) : EventArgs
{
    public AudioEndpointChangeKind ChangeKind { get; } = changeKind;

    public string? EndpointId { get; } = endpointId;

    public AudioEndpointKind? EndpointKind { get; } = endpointKind;
}

/// <summary>A small canonical-audio window suitable for a level meter and waveform.</summary>
public sealed class CaptureMeterEventArgs(
    TimeSpan position,
    float minimum,
    float maximum,
    float rms,
    float peak,
    bool isClipping) : EventArgs
{
    public TimeSpan Position { get; } = position;

    public float Minimum { get; } = minimum;

    public float Maximum { get; } = maximum;

    public float Rms { get; } = rms;

    public float Peak { get; } = peak;

    public bool IsClipping { get; } = isClipping;
}

/// <summary>Metadata for a finalized canonical recording segment.</summary>
public sealed record CapturedAudioSegment(
    string Path,
    TimeSpan Duration,
    long PcmBytes,
    float Peak,
    bool WasInterrupted);

public sealed class CaptureCompletedEventArgs(CapturedAudioSegment segment) : EventArgs
{
    public CapturedAudioSegment Segment { get; } = segment;
}

public sealed class AudioFaultedEventArgs(
    string operation,
    Exception exception,
    bool deviceMayBeUnavailable) : EventArgs
{
    public string Operation { get; } = operation;

    public Exception Exception { get; } = exception;

    public bool DeviceMayBeUnavailable { get; } = deviceMayBeUnavailable;
}

public enum AudioPlaybackState
{
    Stopped,
    Playing,
    Paused,
}

public sealed class PlaybackPositionEventArgs(
    TimeSpan position,
    TimeSpan duration,
    AudioPlaybackState state) : EventArgs
{
    public TimeSpan Position { get; } = position;

    public TimeSpan Duration { get; } = duration;

    public AudioPlaybackState State { get; } = state;
}

public sealed record Mp3ExportResult(
    string OutputPath,
    TimeSpan SourceDuration,
    TimeSpan EncodedDuration,
    long FileBytes);

public class AudioDeviceUnavailableException : InvalidOperationException
{
    public AudioDeviceUnavailableException(string message)
        : base(message)
    {
    }

    public AudioDeviceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AudioBufferOverflowException : IOException
{
    public AudioBufferOverflowException()
        : base("The recording pipeline could not keep up with the input device. Recording was stopped to avoid silently losing audio.")
    {
    }
}

public sealed class UnsupportedCaptureFormatException : NotSupportedException
{
    public UnsupportedCaptureFormatException(string message)
        : base(message)
    {
    }
}
