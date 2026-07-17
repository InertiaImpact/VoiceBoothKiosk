using System.Text;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Infrastructure.Persistence;

public sealed record WaveRecoveryResult(int RecoveredSegmentCount, IReadOnlyList<string> Errors);

/// <summary>
/// Validates and promotes closed canonical WAV partials left by an interrupted capture.
/// A power-loss header with zero/stale RIFF lengths is repaired only after the expected
/// PCM format and a non-empty, block-aligned data payload have been verified.
/// </summary>
public sealed class WaveRecoveryReconciler
{
    public Task<WaveRecoveryResult> ReconcileAsync(
        string sessionDirectory,
        RecordingSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentNullException.ThrowIfNull(session);
        var recovered = 0;
        var errors = new List<string>();

        foreach (var clip in session.ActiveClips)
        {
            foreach (var segment in clip.Segments.Where(segment =>
                         segment.State is SegmentState.Recording or SegmentState.Recoverable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finalPath = RecoveryDiscovery.ResolveContainedPath(sessionDirectory, segment.RelativeWavePath);
                var partialPath = finalPath + ".partial";
                var candidate = File.Exists(finalPath) ? finalPath : File.Exists(partialPath) ? partialPath : null;
                if (candidate is null)
                {
                    continue;
                }

                try
                {
                    var metadata = ValidateCanonicalWave(candidate, repairHeader: candidate == partialPath);
                    if (candidate == partialPath)
                    {
                        if (File.Exists(finalPath))
                        {
                            throw new IOException("A finalized WAV appeared while its partial was being recovered.");
                        }
                        File.Move(partialPath, finalPath, overwrite: false);
                    }

                    if (segment.State == SegmentState.Recording)
                    {
                        if (session.State == SessionState.Active)
                        {
                            session.MarkActiveSegmentRecoverable(clip.Id, "Recovered after an interrupted recording.");
                        }
                        else
                        {
                            segment.MarkRecoverable("Recovered after an interrupted recording.");
                        }
                    }

                    if (session.State is not SessionState.Active and not SessionState.Recoverable)
                    {
                        throw new InvalidDataException($"A segment cannot be recovered while its session is {session.State}.");
                    }

                    session.CompleteRecoveredSegment(
                        clip.Id,
                        segment.Id,
                        metadata.Duration,
                        metadata.DataBytes,
                        checksumSha256: null,
                        new DateTimeOffset(File.GetLastWriteTimeUtc(finalPath), TimeSpan.Zero));
                    recovered++;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or DomainException)
                {
                    errors.Add($"Segment {segment.Id}: {exception.Message}");
                }
            }
        }

        return Task.FromResult(new WaveRecoveryResult(recovered, errors));
    }

    private static RecoveredWaveMetadata ValidateCanonicalWave(string path, bool repairHeader)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            repairHeader ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            repairHeader ? FileOptions.WriteThrough : FileOptions.SequentialScan);
        if (stream.Length < 44)
        {
            throw new InvalidDataException("The interrupted WAV is too small to contain a valid header.");
        }

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (ReadFourCc(reader) != "RIFF")
        {
            throw new InvalidDataException("The interrupted file is not a RIFF WAV.");
        }
        var declaredRiffBytes = reader.ReadUInt32();
        if (ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("The interrupted RIFF file is not WAVE audio.");
        }

        ushort? formatTag = null;
        ushort? channels = null;
        int? sampleRate = null;
        ushort? blockAlign = null;
        ushort? bitsPerSample = null;
        long dataSizeOffset = -1;
        long dataStart = -1;
        uint declaredDataBytes = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSizeOffset = stream.Position;
            var chunkSize = reader.ReadUInt32();
            var chunkStart = stream.Position;
            if (chunkId == "fmt ")
            {
                if (chunkSize < 16 || chunkStart + chunkSize > stream.Length)
                {
                    throw new InvalidDataException("The WAV format chunk is incomplete.");
                }
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                blockAlign = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
            }
            else if (chunkId == "data")
            {
                dataSizeOffset = chunkSizeOffset;
                dataStart = chunkStart;
                declaredDataBytes = chunkSize;
                break;
            }

            var next = chunkStart + chunkSize + (chunkSize & 1);
            if (next > stream.Length)
            {
                throw new InvalidDataException($"The WAV {chunkId} chunk extends beyond the file.");
            }
            stream.Position = next;
        }

        if (formatTag != 1 || channels != 1 || sampleRate != 48_000 || blockAlign != 2 || bitsPerSample != 16)
        {
            throw new InvalidDataException("The interrupted WAV is not 48 kHz, 16-bit mono PCM.");
        }
        if (dataStart < 0 || dataSizeOffset < 0)
        {
            throw new InvalidDataException("The interrupted WAV has no audio-data chunk.");
        }

        var availableBytes = stream.Length - dataStart;
        var dataBytes = declaredDataBytes > 0 && declaredDataBytes <= availableBytes
            ? declaredDataBytes
            : availableBytes;
        dataBytes -= dataBytes % blockAlign.Value;
        if (dataBytes <= 0 || dataBytes > uint.MaxValue)
        {
            throw new InvalidDataException("The interrupted WAV contains no complete PCM samples.");
        }

        var recoveredLength = dataStart + dataBytes;
        if (recoveredLength - 8 > uint.MaxValue)
        {
            throw new InvalidDataException("The interrupted WAV is too large for a RIFF length field.");
        }

        var expectedRiffBytes = (uint)(recoveredLength - 8);
        var needsRepair = declaredRiffBytes != expectedRiffBytes ||
                          declaredDataBytes != dataBytes ||
                          stream.Length != recoveredLength;
        if (needsRepair)
        {
            if (!repairHeader)
            {
                throw new InvalidDataException("The finalized WAV has inconsistent RIFF lengths.");
            }

            stream.SetLength(recoveredLength);
            stream.Position = dataSizeOffset;
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write((uint)dataBytes);
                stream.Position = 4;
                writer.Write(checked((uint)(stream.Length - 8)));
                writer.Flush();
            }
            stream.Flush(flushToDisk: true);
        }

        var duration = TimeSpan.FromSeconds(dataBytes / (double)(sampleRate.Value * blockAlign.Value));
        return new RecoveredWaveMetadata(duration, dataBytes);
    }

    private static string ReadFourCc(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));

    private sealed record RecoveredWaveMetadata(TimeSpan Duration, long DataBytes);
}
