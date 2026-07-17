using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Tests;

public sealed class RecordingSessionTests
{
    [Fact]
    public void Undo_delete_of_only_clip_removes_automatic_empty_replacement()
    {
        var session = CreateSession();
        CompleteSegment(session, TimeSpan.FromSeconds(2));
        var originalId = session.SelectedClipId;
        var replacementId = Guid.NewGuid();

        var deleted = session.DeleteClip(originalId, DateTimeOffset.UtcNow, replacementId);
        Assert.True(deleted.ReplacementCreated);
        Assert.Equal(replacementId, session.SelectedClipId);

        session.UndoDeleteClip(originalId, replacementId);

        var restored = Assert.Single(session.ActiveClips);
        Assert.Equal(originalId, restored.Id);
        Assert.Equal(originalId, session.SelectedClipId);
    }
    [Fact]
    public void Empty_selected_clip_is_reused_instead_of_creating_empty_chains()
    {
        var session = CreateSession();

        var result = session.NewClip(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.False(result.Created);
        Assert.Single(session.ActiveClips);
        Assert.Same(session.SelectedClip, result.Clip);
    }

    [Fact]
    public void Multiple_recording_segments_form_one_logical_clip()
    {
        var session = CreateSession();
        CompleteSegment(session, TimeSpan.FromSeconds(5));
        CompleteSegment(session, TimeSpan.FromSeconds(7));

        Assert.Equal(2, session.SelectedClip.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(12), session.SelectedClip.Duration);
        Assert.Equal(ClipState.Stopped, session.SelectedClip.State);
    }

    [Fact]
    public void Reset_take_returns_segments_and_keeps_clip_identity_and_name()
    {
        var session = CreateSession();
        session.RenameClip(session.SelectedClip.Id, "Opening");
        CompleteSegment(session, TimeSpan.FromSeconds(12));
        var clipId = session.SelectedClip.Id;

        var removed = session.ResetClip(clipId);

        Assert.Single(removed);
        Assert.Equal(clipId, session.SelectedClip.Id);
        Assert.Equal("Opening", session.SelectedClip.DisplayName);
        Assert.False(session.SelectedClip.HasAudio);
        Assert.Equal(ClipState.Empty, session.SelectedClip.State);
    }

    [Fact]
    public void Deleting_only_clip_creates_a_clean_selected_replacement()
    {
        var session = CreateSession();
        CompleteSegment(session, TimeSpan.FromSeconds(2));
        var deletedId = session.SelectedClip.Id;

        var result = session.DeleteClip(deletedId, DateTimeOffset.UtcNow, Guid.NewGuid());

        Assert.True(result.ReplacementCreated);
        Assert.True(result.DeletedClip.IsDeleted);
        Assert.NotEqual(deletedId, session.SelectedClip.Id);
        Assert.Single(session.ActiveClips);
    }

    [Fact]
    public void Export_requires_audio_and_tracks_failure_without_completing_session()
    {
        var session = CreateSession();
        Assert.Throws<DomainException>(session.BeginExport);

        CompleteSegment(session, TimeSpan.FromSeconds(3));
        session.BeginExport();
        Assert.Equal(SessionState.Exporting, session.State);

        session.ExportFailed("Destination unavailable");
        Assert.Equal(SessionState.Active, session.State);
        Assert.Equal("Destination unavailable", session.LastError);
    }

    private static RecordingSession CreateSession() => RecordingSession.Create(
        "Taylor-Topic",
        new DateTimeOffset(2026, 7, 16, 11, 0, 0, TimeSpan.FromHours(-5)),
        new DateTimeOffset(2026, 7, 16, 16, 0, 0, TimeSpan.Zero),
        new SessionSettingsSnapshot());

    private static void CompleteSegment(RecordingSession session, TimeSpan duration)
    {
        var segmentId = Guid.NewGuid();
        session.BeginSegment(session.SelectedClip.Id, segmentId,
            $"segments/{session.SelectedClip.Id}/{segmentId}.wav", null, DateTimeOffset.UtcNow);
        session.CompleteSegment(session.SelectedClip.Id, segmentId, duration, 256, "checksum", DateTimeOffset.UtcNow);
    }
}
