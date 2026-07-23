namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>
/// Removes the leading ID3v2 block emitted by the Windows Media Foundation MP3 sink.
/// The sink can write invalid iTunSMPB padding metadata for 48 kHz mono input, which
/// causes stricter Windows players to reject an otherwise valid MPEG audio stream.
/// Voice Booth does not place user metadata in this block, so retaining only the MP3
/// frames is the safest and most broadly compatible representation.
/// </summary>
internal static class Mp3CompatibilitySanitizer
{
    private const int Id3HeaderLength = 10;
    private const int CopyBufferSize = 128 * 1024;

    public static bool RemoveLeadingId3v2Tag(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var replacementPath = Path.Combine(
            Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("The MP3 path has no parent directory.", nameof(path)),
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.sanitizing");

        try
        {
            long audioOffset;
            using (var source = new FileStream(
                       fullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       CopyBufferSize,
                       FileOptions.SequentialScan))
            {
                Span<byte> header = stackalloc byte[Id3HeaderLength];
                if (source.Read(header) != header.Length || !HasId3Signature(header))
                {
                    return false;
                }

                audioOffset = GetAudioOffset(header, source.Length);
                source.Position = audioOffset;
                Span<byte> frameHeader = stackalloc byte[4];
                if (source.Read(frameHeader) != frameHeader.Length || !IsMpegLayer3FrameHeader(frameHeader))
                {
                    throw new InvalidDataException(
                        "The MP3 encoder metadata was not followed by a valid MPEG Layer III audio frame.");
                }

                source.Position = audioOffset;
                using var replacement = new FileStream(
                    replacementPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.SequentialScan | FileOptions.WriteThrough);
                CopyTo(source, replacement, cancellationToken);
                replacement.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(replacementPath, fullPath, overwrite: true);
            return true;
        }
        finally
        {
            TryDelete(replacementPath);
        }
    }

    private static long GetAudioOffset(ReadOnlySpan<byte> header, long fileLength)
    {
        if ((header[6] & 0x80) != 0 ||
            (header[7] & 0x80) != 0 ||
            (header[8] & 0x80) != 0 ||
            (header[9] & 0x80) != 0)
        {
            throw new InvalidDataException("The MP3 encoder produced an invalid ID3v2 size.");
        }

        var payloadLength =
            (header[6] << 21) |
            (header[7] << 14) |
            (header[8] << 7) |
            header[9];
        var hasFooter = header[3] == 4 && (header[5] & 0x10) != 0;
        var audioOffset = (long)Id3HeaderLength + payloadLength + (hasFooter ? Id3HeaderLength : 0);
        if (audioOffset > fileLength - 4)
        {
            throw new InvalidDataException("The MP3 encoder produced an incomplete ID3v2 block.");
        }

        return audioOffset;
    }

    private static bool HasId3Signature(ReadOnlySpan<byte> header) =>
        header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3';

    private static bool IsMpegLayer3FrameHeader(ReadOnlySpan<byte> header)
    {
        var hasSync = header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
        var version = (header[1] >> 3) & 0x03;
        var layer = (header[1] >> 1) & 0x03;
        var bitrateIndex = (header[2] >> 4) & 0x0F;
        var sampleRateIndex = (header[2] >> 2) & 0x03;
        return hasSync &&
               version != 0x01 &&
               layer == 0x01 &&
               bitrateIndex is > 0 and < 0x0F &&
               sampleRateIndex != 0x03;
    }

    private static void CopyTo(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, bytesRead);
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
}
