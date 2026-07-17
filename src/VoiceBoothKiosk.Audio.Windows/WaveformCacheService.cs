using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>
/// Builds a compact multiresolution min/max cache and selects only the resolution needed by a
/// waveform viewport. Cache construction streams each level and does not retain a whole clip's
/// samples or peaks in memory.
/// </summary>
public sealed class WaveformCacheService : IWaveformService
{
    private const int CacheVersion = 1;
    private const int BaseSamplesPerPeak = 512;
    private const int FixedHeaderBytes = 4 + 4 + 4 + 4 + 8 + 4 + 4;
    private static readonly byte[] Magic = "VBPK"u8.ToArray();

    public Task BuildCacheAsync(
        WaveformCacheBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCanonicalFormat(request.Format);
        return Task.Run(() => BuildCache(request, cancellationToken), cancellationToken);
    }

    public Task<WaveformData> LoadAsync(
        WaveformLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PeakCachePaths.Count == 0)
        {
            return Task.FromResult(new WaveformData([], TimeSpan.Zero, 0));
        }

        if (request.PixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Waveform pixel width must be positive.");
        }

        return Task.Run(() => Load(request, cancellationToken), cancellationToken);
    }

    private static void BuildCache(WaveformCacheBuildRequest request, CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(request.WavePath);
        var destinationPath = Path.GetFullPath(request.PeakCachePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
                                   throw new ArgumentException("The peak-cache path has no parent directory.", nameof(request));
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.partial";

        try
        {
            using var waveReader = new WaveFileReader(sourcePath);
            ValidateWaveFormat(waveReader.WaveFormat, sourcePath);
            var totalSamples = waveReader.Length / waveReader.WaveFormat.BlockAlign;
            var levelCounts = CalculateLevelCounts(totalSamples);

            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       128 * 1024,
                       FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(CacheVersion);
                writer.Write(request.Format.SampleRate);
                writer.Write(request.Format.Channels);
                writer.Write(totalSamples);
                writer.Write(0f); // Absolute peak, rewritten after the base level is complete.
                writer.Write(levelCounts.Length);

                var baseLevel = WriteBaseLevel(waveReader, output, writer, levelCounts[0], cancellationToken);
                var absolutePeak = baseLevel.AbsolutePeak;
                var previous = baseLevel.Level;

                for (var levelIndex = 1; levelIndex < levelCounts.Length; levelIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    previous = WriteReducedLevel(
                        output,
                        writer,
                        previous,
                        checked(previous.SamplesPerPeak * 2),
                        levelCounts[levelIndex],
                        cancellationToken);
                }

                output.Position = 4 + 4 + 4 + 4 + 8;
                writer.Write(absolutePeak);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            // Parse the result before replacing an older known-good cache.
            _ = ReadMetadata(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static BaseLevelResult WriteBaseLevel(
        WaveFileReader waveReader,
        FileStream output,
        BinaryWriter writer,
        int expectedPeakCount,
        CancellationToken cancellationToken)
    {
        writer.Write(BaseSamplesPerPeak);
        writer.Write(expectedPeakCount);
        var dataOffset = output.Position;
        var byteBuffer = new byte[128 * 1024];
        var bufferedBytes = 0;
        var bufferOffset = 0;
        var peaksWritten = 0;
        var samplesInBucket = 0;
        var minimum = 1f;
        var maximum = -1f;
        var absolutePeak = 0f;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bufferOffset + 2 > bufferedBytes)
            {
                bufferedBytes = waveReader.Read(byteBuffer, 0, byteBuffer.Length);
                bufferOffset = 0;
                if (bufferedBytes == 0)
                {
                    break;
                }

                if ((bufferedBytes & 1) != 0)
                {
                    throw new InvalidDataException("The canonical WAV contains a partial 16-bit sample.");
                }
            }

            var pcm = BinaryPrimitives.ReadInt16LittleEndian(byteBuffer.AsSpan(bufferOffset, 2));
            bufferOffset += 2;
            var sample = pcm / 32768f;
            minimum = Math.Min(minimum, sample);
            maximum = Math.Max(maximum, sample);
            absolutePeak = Math.Max(absolutePeak, Math.Abs(sample));
            samplesInBucket++;

            if (samplesInBucket == BaseSamplesPerPeak)
            {
                writer.Write(minimum);
                writer.Write(maximum);
                peaksWritten++;
                samplesInBucket = 0;
                minimum = 1f;
                maximum = -1f;
            }
        }

        if (samplesInBucket > 0)
        {
            writer.Write(minimum);
            writer.Write(maximum);
            peaksWritten++;
        }

        if (peaksWritten != expectedPeakCount)
        {
            throw new InvalidDataException(
                $"Waveform cache expected {expectedPeakCount} base peaks but produced {peaksWritten}.");
        }

        writer.Flush();
        return new BaseLevelResult(
            new CacheLevel(BaseSamplesPerPeak, peaksWritten, dataOffset),
            absolutePeak);
    }

    private static CacheLevel WriteReducedLevel(
        FileStream output,
        BinaryWriter writer,
        CacheLevel sourceLevel,
        int samplesPerPeak,
        int expectedPeakCount,
        CancellationToken cancellationToken)
    {
        var appendPosition = output.Length;
        output.Position = appendPosition;
        writer.Write(samplesPerPeak);
        writer.Write(expectedPeakCount);
        var destinationOffset = output.Position;
        writer.Flush();

        using var input = new FileStream(
            output.Name,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            128 * 1024,
            FileOptions.SequentialScan);
        input.Position = sourceLevel.DataOffset;
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);

        var written = 0;
        for (var sourceIndex = 0; sourceIndex < sourceLevel.PeakCount; sourceIndex += 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var minimum = reader.ReadSingle();
            var maximum = reader.ReadSingle();
            if (sourceIndex + 1 < sourceLevel.PeakCount)
            {
                minimum = Math.Min(minimum, reader.ReadSingle());
                maximum = Math.Max(maximum, reader.ReadSingle());
            }

            writer.Write(minimum);
            writer.Write(maximum);
            written++;
        }

        if (written != expectedPeakCount)
        {
            throw new InvalidDataException(
                $"Waveform cache expected {expectedPeakCount} reduced peaks but produced {written}.");
        }

        writer.Flush();
        return new CacheLevel(samplesPerPeak, written, destinationOffset);
    }

    private static WaveformData Load(WaveformLoadRequest request, CancellationToken cancellationToken)
    {
        var caches = request.PeakCachePaths
            .Select(path => ReadMetadata(Path.GetFullPath(path)))
            .ToArray();
        if (caches.Any(static cache => cache.SampleRate != WasapiAudioCaptureService.CanonicalSampleRate))
        {
            throw new InvalidDataException("Waveform caches use inconsistent sample rates.");
        }

        var totalSamples = caches.Aggregate<CacheMetadata, long>(0, static (sum, cache) => checked(sum + cache.TotalSamples));
        var duration = TimeSpan.FromSeconds((double)totalSamples / WasapiAudioCaptureService.CanonicalSampleRate);
        var requestedStart = request.VisibleStart ?? TimeSpan.Zero;
        var visibleStartSamples = Math.Clamp(
            (long)Math.Floor(requestedStart.TotalSeconds * WasapiAudioCaptureService.CanonicalSampleRate),
            0,
            totalSamples);
        var defaultVisibleSamples = totalSamples - visibleStartSamples;
        var requestedVisibleSamples = request.VisibleDuration is null
            ? defaultVisibleSamples
            : (long)Math.Ceiling(request.VisibleDuration.Value.TotalSeconds * WasapiAudioCaptureService.CanonicalSampleRate);
        var visibleSamples = Math.Clamp(requestedVisibleSamples, 0, defaultVisibleSamples);
        if (visibleSamples == 0)
        {
            return new WaveformData([], duration, caches.Max(static cache => cache.AbsolutePeak));
        }

        var minima = Enumerable.Repeat(1f, request.PixelWidth).ToArray();
        var maxima = Enumerable.Repeat(-1f, request.PixelWidth).ToArray();
        var populated = new bool[request.PixelWidth];
        long logicalSegmentStart = 0;

        foreach (var cache in caches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalSegmentEnd = logicalSegmentStart + cache.TotalSamples;
            var visibleEndSamples = visibleStartSamples + visibleSamples;
            var overlapStart = Math.Max(visibleStartSamples, logicalSegmentStart);
            var overlapEnd = Math.Min(visibleEndSamples, logicalSegmentEnd);
            if (overlapStart < overlapEnd)
            {
                var desiredSamplesPerPixel = Math.Max(1d, (double)visibleSamples / request.PixelWidth);
                var level = SelectLevel(cache.Levels, desiredSamplesPerPixel);
                var localStart = overlapStart - logicalSegmentStart;
                var localEnd = overlapEnd - logicalSegmentStart;
                var firstPeak = Math.Clamp(localStart / level.SamplesPerPeak, 0, level.PeakCount - 1);
                var lastPeakExclusive = Math.Min(
                    level.PeakCount,
                    checked((int)((localEnd + level.SamplesPerPeak - 1) / level.SamplesPerPeak)));

                using var stream = new FileStream(cache.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.Position = level.DataOffset + (firstPeak * 8L);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
                for (var peakIndex = firstPeak; peakIndex < lastPeakExclusive; peakIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var minimum = reader.ReadSingle();
                    var maximum = reader.ReadSingle();
                    var logicalPeakStart = logicalSegmentStart + (peakIndex * level.SamplesPerPeak);
                    var logicalPeakEnd = Math.Min(
                        logicalSegmentEnd,
                        logicalPeakStart + level.SamplesPerPeak);
                    var clippedStart = Math.Max(logicalPeakStart, visibleStartSamples);
                    var clippedEnd = Math.Min(logicalPeakEnd, visibleEndSamples);
                    var firstPixel = Math.Clamp(
                        (int)(((clippedStart - visibleStartSamples) * request.PixelWidth) / visibleSamples),
                        0,
                        request.PixelWidth - 1);
                    var lastPixelExclusive = Math.Clamp(
                        (int)Math.Ceiling(
                            ((double)(clippedEnd - visibleStartSamples) * request.PixelWidth) / visibleSamples),
                        firstPixel + 1,
                        request.PixelWidth);
                    for (var pixel = firstPixel; pixel < lastPixelExclusive; pixel++)
                    {
                        minima[pixel] = Math.Min(minima[pixel], minimum);
                        maxima[pixel] = Math.Max(maxima[pixel], maximum);
                        populated[pixel] = true;
                    }
                }
            }

            logicalSegmentStart = logicalSegmentEnd;
        }

        var peaks = new WaveformPeak[request.PixelWidth];
        for (var pixel = 0; pixel < peaks.Length; pixel++)
        {
            peaks[pixel] = populated[pixel]
                ? new WaveformPeak(minima[pixel], maxima[pixel])
                : new WaveformPeak(0, 0);
        }

        return new WaveformData(peaks, duration, caches.Max(static cache => cache.AbsolutePeak));
    }

    private static CacheMetadata ReadMetadata(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException($"The file is not a Voice Booth waveform cache: {path}");
        }

        var version = reader.ReadInt32();
        if (version != CacheVersion)
        {
            throw new InvalidDataException($"Waveform cache version {version} is not supported: {path}");
        }

        var sampleRate = reader.ReadInt32();
        var channels = reader.ReadInt32();
        var totalSamples = reader.ReadInt64();
        var absolutePeak = reader.ReadSingle();
        var levelCount = reader.ReadInt32();
        if (sampleRate <= 0 || channels != 1 || totalSamples < 0 ||
            !float.IsFinite(absolutePeak) || absolutePeak is < 0 or > 1 ||
            levelCount <= 0 || levelCount > 64)
        {
            throw new InvalidDataException($"The waveform cache header is invalid: {path}");
        }

        var levels = new CacheLevel[levelCount];
        for (var index = 0; index < levelCount; index++)
        {
            var samplesPerPeak = reader.ReadInt32();
            var peakCount = reader.ReadInt32();
            var dataOffset = stream.Position;
            if (samplesPerPeak <= 0 || peakCount <= 0 || dataOffset + (peakCount * 8L) > stream.Length)
            {
                throw new InvalidDataException($"Waveform cache level {index} is invalid: {path}");
            }

            levels[index] = new CacheLevel(samplesPerPeak, peakCount, dataOffset);
            stream.Position += peakCount * 8L;
        }

        if (stream.Position != stream.Length || stream.Length < FixedHeaderBytes)
        {
            throw new InvalidDataException($"The waveform cache has an invalid trailing length: {path}");
        }

        return new CacheMetadata(path, sampleRate, totalSamples, absolutePeak, levels);
    }

    private static CacheLevel SelectLevel(CacheLevel[] levels, double desiredSamplesPerPixel)
    {
        var selected = levels[0];
        foreach (var level in levels)
        {
            if (level.SamplesPerPeak > desiredSamplesPerPixel)
            {
                break;
            }

            selected = level;
        }

        return selected;
    }

    private static int[] CalculateLevelCounts(long totalSamples)
    {
        if (totalSamples <= 0)
        {
            throw new InvalidDataException("An empty WAV file cannot produce a waveform cache.");
        }

        var levels = new List<int>();
        var count = checked((int)((totalSamples + BaseSamplesPerPeak - 1) / BaseSamplesPerPeak));
        while (true)
        {
            levels.Add(count);
            if (count == 1)
            {
                break;
            }

            count = (count + 1) / 2;
        }

        return levels.ToArray();
    }

    private static void ValidateCanonicalFormat(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format != AudioFormat.CanonicalMono)
        {
            throw new NotSupportedException("Waveform caches require 48 kHz, 16-bit, mono PCM audio.");
        }
    }

    private static void ValidateWaveFormat(WaveFormat format, string path)
    {
        if (format.Encoding != WaveFormatEncoding.Pcm ||
            format.SampleRate != WasapiAudioCaptureService.CanonicalSampleRate ||
            format.Channels != WasapiAudioCaptureService.CanonicalChannels ||
            format.BitsPerSample != WasapiAudioCaptureService.CanonicalBitsPerSample)
        {
            throw new InvalidDataException($"The WAV file is not in the canonical recording format: {path}");
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

    private sealed record CacheMetadata(
        string Path,
        int SampleRate,
        long TotalSamples,
        float AbsolutePeak,
        CacheLevel[] Levels);

    private sealed record CacheLevel(int SamplesPerPeak, int PeakCount, long DataOffset);

    private sealed record BaseLevelResult(CacheLevel Level, float AbsolutePeak);
}
