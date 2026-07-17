using NAudio.Wave;

namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>
/// Presents ordered canonical WAV segment files as one seekable PCM stream without joining or
/// rewriting the source files. Only the currently read segment is held open.
/// </summary>
public sealed class LogicalClipWaveStream : WaveStream
{
    private static readonly WaveFormat CanonicalFormat = new(
        WasapiAudioCaptureService.CanonicalSampleRate,
        WasapiAudioCaptureService.CanonicalBitsPerSample,
        WasapiAudioCaptureService.CanonicalChannels);

    private readonly object sync = new();
    private readonly SegmentDescriptor[] segments;
    private readonly long length;
    private WaveFileReader? activeReader;
    private int activeSegmentIndex = -1;
    private long position;
    private bool disposed;

    public LogicalClipWaveStream(IEnumerable<string> orderedSegmentPaths)
    {
        ArgumentNullException.ThrowIfNull(orderedSegmentPaths);
        var descriptors = new List<SegmentDescriptor>();
        long start = 0;

        foreach (var suppliedPath in orderedSegmentPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
            var path = Path.GetFullPath(suppliedPath);
            using var reader = new WaveFileReader(path);
            ValidateFormat(reader.WaveFormat, path);
            if (reader.Length % CanonicalFormat.BlockAlign != 0)
            {
                throw new InvalidDataException($"The WAV segment is not sample-frame aligned: {path}");
            }

            if (reader.Length == 0)
            {
                continue;
            }

            descriptors.Add(new SegmentDescriptor(path, start, reader.Length));
            start = checked(start + reader.Length);
        }

        if (descriptors.Count == 0)
        {
            throw new ArgumentException("A logical clip requires at least one non-empty WAV segment.", nameof(orderedSegmentPaths));
        }

        segments = descriptors.ToArray();
        length = start;
    }

    public override WaveFormat WaveFormat => CanonicalFormat;

    public override long Length => length;

    public override long Position
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return position;
            }
        }
        set
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var clamped = Math.Clamp(value, 0, length);
                position = clamped - (clamped % WaveFormat.BlockAlign);
                CloseActiveReader();
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The requested byte range is outside the destination buffer.");
        }

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (position >= length || count == 0)
            {
                return 0;
            }

            // Audio clients should ask for complete frames. Rounding down keeps a malformed
            // caller from splitting a PCM sample between segment reads.
            count -= count % WaveFormat.BlockAlign;
            if (count == 0)
            {
                return 0;
            }

            var totalRead = 0;
            while (totalRead < count && position < length)
            {
                var segmentIndex = FindSegment(position);
                EnsureReader(segmentIndex);
                var segment = segments[segmentIndex];
                var localPosition = position - segment.StartByte;
                if (activeReader!.Position != localPosition)
                {
                    activeReader.Position = localPosition;
                }

                var available = segment.Length - localPosition;
                var requested = (int)Math.Min(count - totalRead, available);
                var read = activeReader.Read(buffer, offset + totalRead, requested);
                if (read <= 0)
                {
                    // A segment changed after validation. Do not loop forever or silently skip
                    // the inconsistency.
                    throw new EndOfStreamException($"The WAV segment ended earlier than expected: {segment.Path}");
                }

                totalRead += read;
                position += read;

                if (position >= segment.StartByte + segment.Length)
                {
                    CloseActiveReader();
                }
            }

            return totalRead;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (sync)
            {
                if (!disposed)
                {
                    disposed = true;
                    CloseActiveReader();
                }
            }
        }

        base.Dispose(disposing);
    }

    private static void ValidateFormat(WaveFormat format, string path)
    {
        if (format.Encoding != WaveFormatEncoding.Pcm ||
            format.SampleRate != WasapiAudioCaptureService.CanonicalSampleRate ||
            format.BitsPerSample != WasapiAudioCaptureService.CanonicalBitsPerSample ||
            format.Channels != WasapiAudioCaptureService.CanonicalChannels)
        {
            throw new InvalidDataException(
                $"The segment is not 48 kHz, 16-bit, mono PCM and cannot be part of this logical clip: {path}");
        }
    }

    private int FindSegment(long logicalPosition)
    {
        var low = 0;
        var high = segments.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var segment = segments[middle];
            if (logicalPosition < segment.StartByte)
            {
                high = middle - 1;
            }
            else if (logicalPosition >= segment.StartByte + segment.Length)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        throw new InvalidOperationException("The logical playback position does not map to a WAV segment.");
    }

    private void EnsureReader(int segmentIndex)
    {
        if (activeReader is not null && activeSegmentIndex == segmentIndex)
        {
            return;
        }

        CloseActiveReader();
        var segment = segments[segmentIndex];
        activeReader = new WaveFileReader(segment.Path);
        ValidateFormat(activeReader.WaveFormat, segment.Path);
        if (activeReader.Length != segment.Length)
        {
            CloseActiveReader();
            throw new InvalidDataException($"The WAV segment changed after the clip was opened: {segment.Path}");
        }

        activeSegmentIndex = segmentIndex;
    }

    private void CloseActiveReader()
    {
        activeReader?.Dispose();
        activeReader = null;
        activeSegmentIndex = -1;
    }

    private sealed record SegmentDescriptor(string Path, long StartByte, long Length);
}
