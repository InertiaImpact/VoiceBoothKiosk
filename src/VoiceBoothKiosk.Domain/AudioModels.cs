namespace VoiceBoothKiosk.Domain;

public sealed record AudioFormat
{
    public static AudioFormat CanonicalMono { get; } = new(48_000, 1, 16);

    public AudioFormat(int sampleRate, int channels, int bitsPerSample)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Only mono or stereo audio is supported.");
        }

        if (bitsPerSample is not 16)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Version 1 requires 16-bit PCM.");
        }

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
    }

    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public int BytesPerSecond => checked(SampleRate * Channels * (BitsPerSample / 8));
}

public sealed record AudioDeviceSelection
{
    public string? EndpointId { get; init; }
    public string FriendlyNameFallback { get; init; } = string.Empty;
    public AudioDeviceFlow Flow { get; init; }
    public bool UseDefault { get; init; } = true;

    public static AudioDeviceSelection WindowsDefault(AudioDeviceFlow flow) => new() { Flow = flow, UseDefault = true };
}

public sealed record AudioDeviceInfo
{
    public required string EndpointId { get; init; }
    public required string FriendlyName { get; init; }
    public AudioDeviceFlow Flow { get; init; }
    public bool IsDefault { get; init; }
    public bool IsAvailable { get; init; } = true;
    public IReadOnlyList<AudioFormat> SupportedFormats { get; init; } = [];
}
