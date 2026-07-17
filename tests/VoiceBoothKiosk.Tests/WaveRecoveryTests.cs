using NAudio.Wave;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Persistence;

namespace VoiceBoothKiosk.Tests;

public sealed class WaveRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoiceBoothKiosk.WaveRecoveryTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Complete_partial_wave_with_interrupted_header_is_promoted_and_finalized()
    {
        var now = DateTimeOffset.UtcNow;
        var session = RecordingSession.Create("Interrupted", now.ToLocalTime(), now, new SessionSettingsSnapshot());
        var segmentId = Guid.NewGuid();
        var relative = Path.Combine("segments", session.SelectedClipId.ToString("D"), $"{segmentId:D}.wav");
        session.BeginSegment(session.SelectedClipId, segmentId, relative, null, now);
        var finalPath = Path.Combine(_root, relative);
        var partialPath = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        WriteTone(partialPath);

        // Model a power loss after PCM was flushed but before WaveFileWriter rewrote lengths.
        var header = File.ReadAllBytes(partialPath);
        var dataChunkOffset = FindFourCc(header, "data");
        Assert.True(dataChunkOffset >= 0);
        using (var stream = new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = 4;
            writer.Write(0u);
            stream.Position = dataChunkOffset + 4;
            writer.Write(0u);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        var result = await new WaveRecoveryReconciler().ReconcileAsync(_root, session);

        Assert.Equal(1, result.RecoveredSegmentCount);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(partialPath));
        var segment = Assert.Single(session.SelectedClip.Segments);
        Assert.Equal(SegmentState.Finalized, segment.State);
        Assert.InRange(segment.Duration.TotalMilliseconds, 990, 1_010);
        using var reader = new WaveFileReader(finalPath);
        Assert.Equal(96_000, reader.Length);
    }

    [Fact]
    public async Task Complete_partial_wave_with_only_stale_riff_size_repairs_header_before_promotion()
    {
        var now = DateTimeOffset.UtcNow;
        var session = RecordingSession.Create("Interrupted-Riff", now.ToLocalTime(), now, new SessionSettingsSnapshot());
        var segmentId = Guid.NewGuid();
        var relative = Path.Combine("segments", session.SelectedClipId.ToString("D"), $"{segmentId:D}.wav");
        session.BeginSegment(session.SelectedClipId, segmentId, relative, null, now);
        var finalPath = Path.Combine(_root, relative);
        var partialPath = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        WriteTone(partialPath);

        // Model a torn header update where the data length was committed but RIFF length was not.
        using (var stream = new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = 4;
            writer.Write(0u);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        var result = await new WaveRecoveryReconciler().ReconcileAsync(_root, session);

        Assert.Equal(1, result.RecoveredSegmentCount);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(finalPath));
        using (var stream = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream))
        {
            stream.Position = 4;
            Assert.Equal((uint)(stream.Length - 8), reader.ReadUInt32());
        }
        using var waveReader = new WaveFileReader(finalPath);
        Assert.Equal(96_000, waveReader.Length);
    }

    private static void WriteTone(string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(48_000, 16, 1));
        for (var index = 0; index < 48_000; index++)
        {
            writer.WriteSample((float)(Math.Sin(2 * Math.PI * 220 * index / 48_000d) * 0.25));
        }
    }

    private static int FindFourCc(IReadOnlyList<byte> bytes, string value)
    {
        var expected = System.Text.Encoding.ASCII.GetBytes(value);
        for (var index = 0; index <= bytes.Count - expected.Length; index++)
        {
            if (Enumerable.Range(0, expected.Length).All(offset => bytes[index + offset] == expected[offset]))
            {
                return index;
            }
        }
        return -1;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
