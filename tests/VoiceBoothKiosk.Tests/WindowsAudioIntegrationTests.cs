using System.Text.Json;
using NAudio.Wave;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Audio.Windows;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Tests;

public sealed class WindowsAudioIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoiceBoothKiosk.AudioTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Export_name_discovery_reserves_stale_marker_and_partial_folder_names_after_restart()
    {
        var destinationRoot = Path.Combine(_root, "stale-exports");
        Directory.CreateDirectory(destinationRoot);
        var startedLocal = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(-5));
        var session = RecordingSession.Create(
            "Restart-Collision",
            startedLocal,
            startedLocal.ToUniversalTime(),
            new SessionSettingsSnapshot());
        var baseName = "Restart-Collision_07162026";
        var markerPath = Path.Combine(destinationRoot, $".{baseName}.voicebooth-reservation");
        var partialDirectoryName = $".{baseName}_02.{Guid.NewGuid():N}.partial";
        File.WriteAllText(markerPath, string.Empty);
        Directory.CreateDirectory(Path.Combine(destinationRoot, partialDirectoryName));

        var service = new MediaFoundationExportService();
        var destination = new DestinationPreset
        {
            Id = "test",
            Label = "Test",
            RootPath = destinationRoot,
            MinimumFreeBytes = 0
        };
        var existingNames = await service.GetExistingSessionFolderNamesAsync(destination);
        var plan = OutputNamePlanner.Create(session, existingNames);

        Assert.Contains(baseName, existingNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains($"{baseName}_02", existingNames, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(partialDirectoryName, existingNames, StringComparer.OrdinalIgnoreCase);
        Assert.Equal($"{baseName}_03", plan.SessionFolderName);
    }

    [Fact]
    public async Task Waveform_cache_round_trips_a_generated_voice_format_wave()
    {
        Directory.CreateDirectory(_root);
        var wavePath = Path.Combine(_root, "tone.wav");
        var peaksPath = Path.Combine(_root, "tone.peaks");
        WriteTone(wavePath, TimeSpan.FromSeconds(1));
        var service = new WaveformCacheService();

        await service.BuildCacheAsync(new(wavePath, peaksPath, AudioFormat.CanonicalMono));
        var result = await service.LoadAsync(new([peaksPath], 320));

        Assert.True(File.Exists(peaksPath));
        Assert.InRange(result.Duration.TotalMilliseconds, 990, 1_010);
        Assert.NotEmpty(result.Peaks);
        Assert.True(result.AbsolutePeak > 0.1f);
    }

    [Fact]
    public async Task Media_foundation_exports_and_verifies_mp3_and_lossless_raw_wave_transactionally()
    {
        Directory.CreateDirectory(_root);
        var wavePath = Path.Combine(_root, "source.wav");
        var destinationRoot = Path.Combine(_root, "exports");
        Directory.CreateDirectory(destinationRoot);
        WriteTone(wavePath, TimeSpan.FromSeconds(1));
        var service = new MediaFoundationExportService();
        var clipId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var request = new ExportRequest
        {
            SessionId = Guid.NewGuid(),
            SessionDisplayName = "Audio integration",
            Destination = new DestinationPreset
            {
                Id = "test",
                Label = "Test",
                RootPath = destinationRoot,
                MinimumFreeBytes = 0
            },
            SessionFolderName = "Audio-integration_07162026",
            Format = AudioFormat.CanonicalMono,
            Mp3BitrateKbps = 128,
            Clips =
            [
                new ClipExportSource
                {
                    ClipId = clipId,
                    OutputFileName = "Audio-integration_01_07162026.mp3",
                    Duration = TimeSpan.FromSeconds(1),
                    Segments =
                    [
                        new PlaybackSegmentSource
                        {
                            SegmentId = segmentId,
                            WavePath = wavePath,
                            Duration = TimeSpan.FromSeconds(1)
                        }
                    ]
                }
            ]
        };

        var result = await service.ExportAsync(request);

        var mp3 = Assert.Single(result.ExportedFilePaths);
        var rawWave = Assert.Single(result.RawFilePaths);
        Assert.True(File.Exists(mp3));
        Assert.True(File.Exists(rawWave));
        Assert.Equal("Raw", Path.GetFileName(Path.GetDirectoryName(rawWave)));
        Assert.Equal("Audio-integration_01_07162026.wav", Path.GetFileName(rawWave));
        Assert.DoesNotContain(Directory.EnumerateDirectories(destinationRoot),
            path => Path.GetFileName(path).StartsWith(".voicebooth-", StringComparison.OrdinalIgnoreCase));
        var mp3Header = new byte[4];
        using (var headerReader = File.OpenRead(mp3))
        {
            Assert.Equal(mp3Header.Length, headerReader.Read(mp3Header));
        }
        Assert.Equal(0xFF, mp3Header[0]);
        Assert.Equal(0xE0, mp3Header[1] & 0xE0);
        Assert.Equal(0x01, (mp3Header[1] >> 1) & 0x03);
        using var reader = new Mp3FileReader(mp3);
        Assert.InRange(reader.TotalTime.TotalMilliseconds, 900, 1_150);
        using (var rawReader = new WaveFileReader(rawWave))
        {
            Assert.Equal(WaveFormatEncoding.Pcm, rawReader.WaveFormat.Encoding);
            Assert.Equal(48_000, rawReader.WaveFormat.SampleRate);
            Assert.Equal(16, rawReader.WaveFormat.BitsPerSample);
            Assert.Equal(1, rawReader.WaveFormat.Channels);
            Assert.Equal(96_000, rawReader.Length);
            Assert.InRange(rawReader.TotalTime.TotalMilliseconds, 999, 1_001);
        }

        var recovered = await service.FindCommittedExportAsync(
            request.SessionId,
            [segmentId],
            [request.Destination]);
        Assert.NotNull(recovered);
        Assert.Equal(result.SessionFolderPath, recovered.SessionFolderPath);
        Assert.Equal(result.ExportedFilePaths, recovered.ExportedFilePaths);
        Assert.Equal(result.RawFilePaths, recovered.RawFilePaths);

        var changedSession = await service.FindCommittedExportAsync(
            request.SessionId,
            [Guid.NewGuid()],
            [request.Destination]);
        Assert.Null(changedSession);
    }

    [Fact]
    public async Task Media_foundation_exports_multiple_paused_segments_and_multiple_clips()
    {
        Directory.CreateDirectory(_root);
        var firstSegmentPath = Path.Combine(_root, "first-segment.wav");
        var resumedSegmentPath = Path.Combine(_root, "resumed-segment.wav");
        var secondClipPath = Path.Combine(_root, "second-clip.wav");
        var destinationRoot = Path.Combine(_root, "paused-exports");
        Directory.CreateDirectory(destinationRoot);
        WriteTone(firstSegmentPath, TimeSpan.FromSeconds(0.5));
        WriteTone(resumedSegmentPath, TimeSpan.FromSeconds(0.75));
        WriteTone(secondClipPath, TimeSpan.FromSeconds(0.5));

        var firstClipId = Guid.NewGuid();
        var secondClipId = Guid.NewGuid();
        var request = new ExportRequest
        {
            SessionId = Guid.NewGuid(),
            SessionDisplayName = "Paused recording integration",
            Destination = new DestinationPreset
            {
                Id = "test",
                Label = "Test",
                RootPath = destinationRoot,
                MinimumFreeBytes = 0
            },
            SessionFolderName = "Paused-recording_07162026",
            Format = AudioFormat.CanonicalMono,
            Mp3BitrateKbps = 128,
            Clips =
            [
                new ClipExportSource
                {
                    ClipId = firstClipId,
                    OutputFileName = "Paused-recording_01_07162026.mp3",
                    Duration = TimeSpan.FromSeconds(1.25),
                    Segments =
                    [
                        new PlaybackSegmentSource
                        {
                            SegmentId = Guid.NewGuid(),
                            WavePath = firstSegmentPath,
                            Duration = TimeSpan.FromSeconds(0.5)
                        },
                        new PlaybackSegmentSource
                        {
                            SegmentId = Guid.NewGuid(),
                            WavePath = resumedSegmentPath,
                            Duration = TimeSpan.FromSeconds(0.75)
                        }
                    ]
                },
                new ClipExportSource
                {
                    ClipId = secondClipId,
                    OutputFileName = "Paused-recording_02_07162026.mp3",
                    Duration = TimeSpan.FromSeconds(0.5),
                    Segments =
                    [
                        new PlaybackSegmentSource
                        {
                            SegmentId = Guid.NewGuid(),
                            WavePath = secondClipPath,
                            Duration = TimeSpan.FromSeconds(0.5)
                        }
                    ]
                }
            ]
        };

        var result = await new MediaFoundationExportService().ExportAsync(request);

        Assert.Equal(2, result.ExportedFilePaths.Count);
        Assert.Equal(2, result.RawFilePaths.Count);
        using var firstRawReader = new WaveFileReader(result.RawFilePaths.Single(path =>
            Path.GetFileName(path) == "Paused-recording_01_07162026.wav"));
        using var secondRawReader = new WaveFileReader(result.RawFilePaths.Single(path =>
            Path.GetFileName(path) == "Paused-recording_02_07162026.wav"));
        Assert.Equal(120_000, firstRawReader.Length);
        Assert.Equal(48_000, secondRawReader.Length);
    }

    [Fact]
    public void Mp3_compatibility_sanitizer_removes_only_the_leading_id3v2_block()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "tagged.mp3");
        byte[] audioFrames =
        [
            0xFF, 0xFB, 0x94, 0xC0,
            0x10, 0x20, 0x30, 0x40,
            0x50, 0x60, 0x70, 0x80
        ];
        byte[] taggedFile =
        [
            (byte)'I', (byte)'D', (byte)'3', 0x03, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x04,
            0xDE, 0xAD, 0xBE, 0xEF,
            .. audioFrames
        ];
        File.WriteAllBytes(path, taggedFile);

        var changed = Mp3CompatibilitySanitizer.RemoveLeadingId3v2Tag(path);

        Assert.True(changed);
        Assert.Equal(audioFrames, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), candidate =>
            Path.GetFileName(candidate).EndsWith(".sanitizing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mp3_compatibility_sanitizer_preserves_an_already_tagless_file()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "tagless.mp3");
        byte[] original = [0xFF, 0xFB, 0x94, 0xC0, 0x01, 0x02, 0x03, 0x04];
        File.WriteAllBytes(path, original);

        var changed = Mp3CompatibilitySanitizer.RemoveLeadingId3v2Tag(path);

        Assert.False(changed);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Committed_export_discovery_rejects_null_empty_and_unreadable_receipts()
    {
        var destinationRoot = Path.Combine(_root, "receipt-validation");
        var folderName = "Receipt-validation_07162026";
        var folder = Path.Combine(destinationRoot, folderName);
        Directory.CreateDirectory(folder);
        var receiptPath = Path.Combine(folder, ".voicebooth-export.json");
        var sessionId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var destination = new DestinationPreset
        {
            Id = "test",
            Label = "Test",
            RootPath = destinationRoot,
            MinimumFreeBytes = 0
        };
        var service = new MediaFoundationExportService();

        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            SessionId = sessionId,
            SessionFolderName = folderName,
            FinalizedSegmentIds = (Guid[]?)null,
            FileNames = (string[]?)null
        }));
        Assert.Null(await service.FindCommittedExportAsync(sessionId, [segmentId], [destination]));

        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            SessionId = sessionId,
            SessionFolderName = folderName,
            FinalizedSegmentIds = Array.Empty<Guid>(),
            FileNames = Array.Empty<string>()
        }));
        Assert.Null(await service.FindCommittedExportAsync(sessionId, [segmentId], [destination]));

        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            SessionId = sessionId,
            SessionFolderName = folderName,
            FinalizedSegmentIds = new[] { segmentId },
            FileNames = new[] { "missing.mp3" }
        }));
        Assert.Null(await service.FindCommittedExportAsync(sessionId, [segmentId], [destination]));

        await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 2,
            SessionId = sessionId,
            SessionFolderName = folderName,
            FinalizedSegmentIds = new[] { segmentId },
            FileNames = new[] { "missing.mp3" },
            RawFileNames = Array.Empty<string>()
        }));
        Assert.Null(await service.FindCommittedExportAsync(sessionId, [segmentId], [destination]));
    }

    private static void WriteTone(string path, TimeSpan duration)
    {
        var format = new WaveFormat(48_000, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        var sampleCount = (int)(format.SampleRate * duration.TotalSeconds);
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * 440 * index / format.SampleRate) * 0.35);
            writer.WriteSample(sample);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
