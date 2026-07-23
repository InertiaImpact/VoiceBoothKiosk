using NAudio.MediaFoundation;
using NAudio.Wave;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>Encodes one logical canonical clip to a verified MP3 using Windows Media Foundation.</summary>
public sealed class MediaFoundationMp3Exporter
{
    private static readonly object MediaFoundationSync = new();

    public IReadOnlyList<int> GetSupportedBitrates()
    {
        EnsureWindows();
        lock (MediaFoundationSync)
        {
            try
            {
                return MediaFoundationEncoder
                    .GetEncodeBitrates(
                        AudioSubtypes.MFAudioFormat_MP3,
                        WasapiAudioCaptureService.CanonicalSampleRate,
                        WasapiAudioCaptureService.CanonicalChannels)
                    .Distinct()
                    .OrderBy(static bitrate => bitrate)
                    .ToArray();
            }
            catch (Exception exception)
            {
                throw new PlatformNotSupportedException(
                    "The Windows Media Foundation MP3 encoder is not available. Install the Media Feature Pack on Windows N images.",
                    exception);
            }
        }
    }

    public Task<Mp3ExportResult> ExportAsync(
        IReadOnlyList<string> orderedWaveSegments,
        string outputPath,
        int bitrateBitsPerSecond = 128_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedWaveSegments);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (orderedWaveSegments.Count == 0)
        {
            throw new ArgumentException("MP3 export requires at least one WAV segment.", nameof(orderedWaveSegments));
        }

        if (bitrateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateBitsPerSecond));
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(fullOutputPath), ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The MP3 output path must have a .mp3 extension.", nameof(outputPath));
        }

        var segmentPaths = orderedWaveSegments.Select(Path.GetFullPath).ToArray();
        return Task.Run(
            () => Export(segmentPaths, fullOutputPath, bitrateBitsPerSecond, cancellationToken),
            cancellationToken);
    }

    private Mp3ExportResult Export(
        string[] orderedWaveSegments,
        string outputPath,
        int bitrate,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var supportedBitrates = GetSupportedBitrates();
        if (!supportedBitrates.Contains(bitrate))
        {
            throw new PlatformNotSupportedException(
                $"The Windows MP3 encoder does not offer the requested {bitrate / 1000} kbps profile at 48 kHz mono. " +
                $"Available profiles: {string.Join(", ", supportedBitrates.Select(static value => $"{value / 1000} kbps"))}.");
        }

        var directory = Path.GetDirectoryName(outputPath) ??
                        throw new ArgumentException("The MP3 output path has no parent directory.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath))
        {
            throw new IOException($"The MP3 output file already exists: {outputPath}");
        }

        // Media Foundation selects its sink from the filename extension, so the
        // transactional working file must still end in .mp3.
        var partialPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.partial.mp3");
        try
        {
            using var source = new LogicalClipWaveStream(orderedWaveSegments);
            var sourceDuration = source.TotalTime;
            var cancelableSource = new CancellationWaveProvider(source, cancellationToken);
            lock (MediaFoundationSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MediaFoundationEncoder.EncodeToMp3(cancelableSource, partialPath, bitrate);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Mp3CompatibilitySanitizer.RemoveLeadingId3v2Tag(partialPath, cancellationToken);

            TimeSpan encodedDuration;
            using (var reader = new MediaFoundationReader(partialPath))
            {
                encodedDuration = reader.TotalTime;
                if (reader.Length <= 0 || encodedDuration <= TimeSpan.Zero)
                {
                    throw new InvalidDataException("The Windows MP3 encoder produced an unreadable or empty file.");
                }
            }

            var durationTolerance = TimeSpan.FromSeconds(Math.Max(1.5, sourceDuration.TotalSeconds * 0.03));
            if ((encodedDuration - sourceDuration).Duration() > durationTolerance)
            {
                throw new InvalidDataException(
                    $"The encoded MP3 duration ({encodedDuration}) differs from its source ({sourceDuration}) beyond tolerance.");
            }

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

            File.Move(partialPath, outputPath, overwrite: false);
            return new Mp3ExportResult(
                outputPath,
                sourceDuration,
                encodedDuration,
                new FileInfo(outputPath).Length);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Media Foundation MP3 encoding is available only on Windows.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class CancellationWaveProvider(
        IWaveProvider source,
        CancellationToken cancellationToken) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return source.Read(buffer, offset, count);
        }
    }
}
