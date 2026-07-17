using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Tests;

public sealed class FileNamingTests
{
    [Theory]
    [InlineData(" Jordan / Election? ", "Jordan-Election")]
    [InlineData("A   name___with---gaps", "A-name-with-gaps")]
    [InlineData("trailing...", "trailing")]
    [InlineData("NUL", "Session")]
    public void Safe_component_handles_windows_names(string source, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.ToSafeComponent(source, "Session"));
    }

    [Fact]
    public void Safe_component_does_not_split_unicode_surrogate_pairs()
    {
        var result = FileNameSanitizer.ToSafeComponent("🎙️🎙️🎙️", "Session", 5);

        Assert.True(result.Length <= 5);
        Assert.DoesNotContain('\uFFFD', result);
    }

    [Fact]
    public void Output_plan_uses_ordinals_custom_names_and_collision_suffixes()
    {
        var session = CreateSession("Jordan-Election");
        AddAudio(session, session.SelectedClip, TimeSpan.FromSeconds(5));
        var second = session.NewClip(Guid.Parse("00000000-0000-0000-0000-000000000002"), DateTimeOffset.UtcNow).Clip;
        session.RenameClip(second.Id, "Intro");
        AddAudio(session, second, TimeSpan.FromSeconds(8));
        var third = session.NewClip(Guid.Parse("00000000-0000-0000-0000-000000000003"), DateTimeOffset.UtcNow).Clip;
        session.RenameClip(third.Id, "Intro");
        AddAudio(session, third, TimeSpan.FromSeconds(9));

        var result = OutputNamePlanner.Create(session, ["Jordan-Election_07162026"]);

        Assert.Equal("Jordan-Election_07162026_02", result.SessionFolderName);
        Assert.Collection(result.ClipFiles,
            item => Assert.Equal("Jordan-Election_01_07162026.mp3", item.FileName),
            item => Assert.Equal("Jordan-Election_Intro_07162026.mp3", item.FileName),
            item => Assert.Equal("Jordan-Election_Intro_02_07162026.mp3", item.FileName));
    }

    private static RecordingSession CreateSession(string name) => RecordingSession.Create(
        name,
        new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(-5)),
        new DateTimeOffset(2026, 7, 16, 15, 0, 0, TimeSpan.Zero),
        new SessionSettingsSnapshot(),
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("00000000-0000-0000-0000-000000000001"));

    private static void AddAudio(RecordingSession session, RecordingClip clip, TimeSpan duration)
    {
        var segmentId = Guid.NewGuid();
        session.BeginSegment(clip.Id, segmentId, $"segments/{clip.Id}/{segmentId}.wav", null, DateTimeOffset.UtcNow);
        session.CompleteSegment(clip.Id, segmentId, duration, 128, null, DateTimeOffset.UtcNow);
    }
}
