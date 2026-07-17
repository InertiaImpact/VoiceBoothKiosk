using System.Globalization;
using System.Text.Json.Serialization;

namespace VoiceBoothKiosk.Domain;

public sealed record NewClipResult(RecordingClip Clip, bool Created);
public sealed record DeleteClipResult(RecordingClip DeletedClip, RecordingClip SelectedClip, bool ReplacementCreated);

public sealed class RecordingSession
{
    private readonly List<RecordingClip> _clips;

    [JsonConstructor]
    public RecordingSession(
        Guid id,
        string displayName,
        string safeName,
        DateTimeOffset startedAtLocal,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        SessionState state,
        IReadOnlyList<RecordingClip>? clips,
        Guid selectedClipId,
        string? destinationId,
        SessionSettingsSnapshot settingsSnapshot,
        string? lastError)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A session ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeName);
        Id = id;
        DisplayName = displayName.Trim();
        SafeName = safeName;
        StartedAtLocal = startedAtLocal;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        State = state;
        _clips = clips?.OrderBy(clip => clip.Ordinal).ToList() ?? [];
        SelectedClipId = selectedClipId;
        DestinationId = destinationId;
        SettingsSnapshot = settingsSnapshot ?? throw new ArgumentNullException(nameof(settingsSnapshot));
        LastError = lastError;
    }

    public Guid Id { get; private set; }
    public string DisplayName { get; private set; }
    public string SafeName { get; private set; }
    public DateTimeOffset StartedAtLocal { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public SessionState State { get; private set; }
    public IReadOnlyList<RecordingClip> Clips => _clips;
    public Guid SelectedClipId { get; private set; }
    public string? DestinationId { get; private set; }
    public SessionSettingsSnapshot SettingsSnapshot { get; private set; }
    public string? LastError { get; private set; }
    public RecordingClip SelectedClip => ActiveClips.SingleOrDefault(clip => clip.Id == SelectedClipId)
        ?? throw new DomainException("The selected clip is unavailable.");
    public IReadOnlyList<RecordingClip> ActiveClips => _clips.Where(clip => !clip.IsDeleted).ToArray();
    [JsonIgnore]
    public bool HasUnresolvedSegments => ActiveClips
        .SelectMany(static clip => clip.Segments)
        .Any(static segment => segment.State != SegmentState.Finalized);
    public TimeSpan TotalDuration => TimeSpan.FromTicks(ActiveClips.Sum(clip => clip.Duration.Ticks));

    public static RecordingSession Create(
        string displayName,
        DateTimeOffset localNow,
        DateTimeOffset utcNow,
        SessionSettingsSnapshot settings,
        Guid? sessionId = null,
        Guid? firstClipId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedDisplayName = displayName.Trim();
        var safeName = FileNameSanitizer.ToSafeComponent(normalizedDisplayName, "Session");
        var id = sessionId.GetValueOrDefault(Guid.NewGuid());
        var clip = RecordingClip.Create(
            firstClipId.GetValueOrDefault(Guid.NewGuid()),
            1,
            FormatDefaultClipName(settings.DefaultClipNameFormat, 1),
            utcNow);
        return new(id, normalizedDisplayName, safeName, localNow, utcNow, null, SessionState.Active,
            [clip], clip.Id, null, settings, null);
    }

    public NewClipResult NewClip(Guid clipId, DateTimeOffset utcNow)
    {
        EnsureActiveAndIdle();
        if (!SelectedClip.HasAudio && SelectedClip.State == ClipState.Empty)
        {
            return new(SelectedClip, false);
        }

        var ordinal = _clips.Count == 0 ? 1 : _clips.Max(clip => clip.Ordinal) + 1;
        var clip = RecordingClip.Create(clipId, ordinal, FormatDefaultClipName(SettingsSnapshot.DefaultClipNameFormat, ordinal), utcNow);
        _clips.Add(clip);
        SelectedClipId = clip.Id;
        return new(clip, true);
    }

    public void SelectClip(Guid clipId)
    {
        EnsureActiveAndIdle();
        var clip = RequireActiveClip(clipId);
        SelectedClipId = clip.Id;
    }

    public void RenameClip(Guid clipId, string? displayName)
    {
        EnsureActiveAndIdle();
        var clip = RequireActiveClip(clipId);
        clip.Rename(displayName, FormatDefaultClipName(SettingsSnapshot.DefaultClipNameFormat, clip.Ordinal));
    }

    public DeleteClipResult DeleteClip(Guid clipId, DateTimeOffset utcNow, Guid replacementClipId)
    {
        EnsureActiveAndIdle();
        var clip = RequireActiveClip(clipId);
        clip.MarkDeleted(utcNow);

        var replacementCreated = false;
        RecordingClip selected;
        if (ActiveClips.Count == 0)
        {
            var ordinal = _clips.Max(item => item.Ordinal) + 1;
            selected = RecordingClip.Create(replacementClipId, ordinal,
                FormatDefaultClipName(SettingsSnapshot.DefaultClipNameFormat, ordinal), utcNow);
            _clips.Add(selected);
            replacementCreated = true;
            SelectedClipId = selected.Id;
        }
        else if (SelectedClipId == clip.Id)
        {
            selected = ActiveClips[0];
            SelectedClipId = selected.Id;
        }
        else
        {
            selected = RequireActiveClip(SelectedClipId);
        }

        return new(clip, selected, replacementCreated);
    }

    public void UndoDeleteClip(Guid clipId, Guid? replacementClipId = null)
    {
        EnsureActiveAndIdle();
        var clip = _clips.SingleOrDefault(item => item.Id == clipId)
            ?? throw new DomainException($"Clip {clipId} does not belong to this session.");
        if (replacementClipId is not null)
        {
            var replacement = _clips.SingleOrDefault(item => item.Id == replacementClipId.Value)
                ?? throw new DomainException("The automatic replacement clip is unavailable.");
            if (replacement.State != ClipState.Empty || replacement.Segments.Count != 0 || replacement.IsCustomNamed)
            {
                throw new DomainException("The automatic replacement clip was changed and the deletion can no longer be undone.");
            }
            _clips.Remove(replacement);
        }
        clip.RestoreDeleted();
        SelectedClipId = clip.Id;
    }

    public IReadOnlyList<RecordingSegment> ResetClip(Guid clipId)
    {
        EnsureActiveAndIdle();
        return RequireActiveClip(clipId).ResetTake();
    }

    public void RestoreClipTake(Guid clipId, IEnumerable<RecordingSegment> segments)
    {
        EnsureActiveAndIdle();
        RequireActiveClip(clipId).RestoreTake(segments);
    }

    public RecordingSegment BeginSegment(
        Guid clipId,
        Guid segmentId,
        string relativeWavePath,
        string? relativePeaksPath,
        DateTimeOffset startedAtUtc)
    {
        EnsureActive();
        if (ActiveClips.Any(clip => clip.State is ClipState.Recording or ClipState.Playing or ClipState.Paused))
        {
            throw new DomainException("Another audio operation is already active.");
        }

        var clip = RequireActiveClip(clipId);
        SelectedClipId = clip.Id;
        return clip.BeginSegment(segmentId, relativeWavePath, relativePeaksPath,
            SettingsSnapshot.RecordingFormat, startedAtUtc);
    }

    public void CompleteSegment(Guid clipId, Guid segmentId, TimeSpan duration, long byteCount, string? checksumSha256, DateTimeOffset completedAtUtc)
    {
        EnsureActive();
        RequireActiveClip(clipId).CompleteSegment(segmentId, duration, byteCount, checksumSha256, completedAtUtc);
    }

    public void BeginPlayback(Guid clipId)
    {
        EnsureActiveAndIdle();
        var clip = RequireActiveClip(clipId);
        SelectedClipId = clip.Id;
        clip.BeginPlayback();
    }

    public void PausePlayback() => SelectedClip.PausePlayback();
    public void ResumePlayback() => SelectedClip.ResumePlayback();
    public void StopPlayback() => SelectedClip.StopPlayback();

    public void SelectDestination(string destinationId)
    {
        EnsureActiveAndIdle();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        DestinationId = destinationId.Trim();
    }

    public void SetInputDevice(AudioDeviceSelection selection)
    {
        EnsureActiveAndIdle();
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Flow != AudioDeviceFlow.Input)
        {
            throw new ArgumentException("The selected device must be an input device.", nameof(selection));
        }

        SettingsSnapshot = SettingsSnapshot with { InputDevice = selection };
    }

    public void SetOutputDevice(AudioDeviceSelection selection)
    {
        EnsureActiveAndIdle();
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Flow != AudioDeviceFlow.Output)
        {
            throw new ArgumentException("The selected device must be an output device.", nameof(selection));
        }

        SettingsSnapshot = SettingsSnapshot with { OutputDevice = selection };
    }

    public void FailSegment(Guid clipId, Guid segmentId, string error)
    {
        EnsureActive();
        RequireActiveClip(clipId).MarkSegmentFailed(segmentId, error);
        LastError = error;
    }

    public void MarkActiveSegmentRecoverable(Guid clipId, string? error)
    {
        EnsureActive();
        RequireActiveClip(clipId).MarkActiveSegmentRecoverable(error);
        LastError = error;
    }

    public void CompleteRecoveredSegment(Guid clipId, Guid segmentId, TimeSpan duration, long byteCount, string? checksumSha256, DateTimeOffset completedAtUtc)
    {
        if (State is not SessionState.Recoverable and not SessionState.Active)
        {
            throw new DomainException("Recovered segments can only be finalized in an active or recoverable session.");
        }

        RequireActiveClip(clipId).CompleteRecoveredSegment(segmentId, duration, byteCount, checksumSha256, completedAtUtc);
    }

    public void BeginExport()
    {
        EnsureCanExport();

        State = SessionState.Exporting;
        LastError = null;
    }

    public void EnsureCanExport()
    {
        EnsureActiveAndIdle();
        EnsureAllActiveSegmentsFinalized();
        if (!ActiveClips.Any(clip => clip.HasAudio))
        {
            throw new DomainException("A session with no recorded clips cannot be exported.");
        }
    }

    public void ExportFailed(string error)
    {
        if (State != SessionState.Exporting)
        {
            throw new DomainException("Only an exporting session can report an export failure.");
        }

        State = SessionState.Active;
        LastError = string.IsNullOrWhiteSpace(error) ? "Export failed." : error.Trim();
    }

    public void ExportCanceled()
    {
        if (State != SessionState.Exporting)
        {
            throw new DomainException("Only an exporting session can cancel export.");
        }

        State = SessionState.Active;
        LastError = null;
    }

    public void Complete(DateTimeOffset completedAtUtc)
    {
        if (State != SessionState.Exporting)
        {
            throw new DomainException("Only an exporting session can be completed.");
        }
        EnsureAllActiveSegmentsFinalized();

        State = SessionState.Completed;
        CompletedAtUtc = completedAtUtc;
        LastError = null;
    }

    public void CompleteRecoveredExport(DateTimeOffset completedAtUtc)
    {
        if (State is not SessionState.Active and not SessionState.Exporting and not SessionState.Recoverable)
        {
            throw new DomainException($"A committed export cannot be recovered from {State} state.");
        }
        EnsureAllActiveSegmentsFinalized();
        if (!ActiveClips.Any(clip => clip.HasAudio))
        {
            throw new DomainException("A committed export cannot be recovered for a session with no audio.");
        }

        foreach (var clip in ActiveClips)
        {
            clip.NormalizeAfterRecovery();
        }
        State = SessionState.Completed;
        CompletedAtUtc = completedAtUtc;
        LastError = null;
    }

    public void MarkRecoverable(string? error)
    {
        if (State is SessionState.Completed or SessionState.Quarantined)
        {
            throw new DomainException($"A {State} session cannot become recoverable.");
        }

        foreach (var clip in _clips)
        {
            clip.NormalizeAfterRecovery();
        }

        State = SessionState.Recoverable;
        LastError = error;
    }

    public void ResumeRecovered()
    {
        if (State != SessionState.Recoverable)
        {
            throw new DomainException("Only a recoverable session can be resumed.");
        }

        State = SessionState.Active;
        LastError = null;
        var selected = ActiveClips.FirstOrDefault(clip => clip.Id == SelectedClipId) ?? ActiveClips.FirstOrDefault();
        if (selected is null)
        {
            throw new DomainException("A recovered session has no active clips.");
        }

        SelectedClipId = selected.Id;
    }

    public void Quarantine()
    {
        if (State is SessionState.Completed or SessionState.Quarantined)
        {
            throw new DomainException($"A {State} session cannot be quarantined.");
        }

        State = SessionState.Quarantined;
    }

    private RecordingClip RequireActiveClip(Guid id) => ActiveClips.SingleOrDefault(clip => clip.Id == id)
        ?? throw new DomainException($"Clip {id} is unavailable.");

    private void EnsureActive()
    {
        if (State != SessionState.Active)
        {
            throw new DomainException($"The session must be active; it is {State}.");
        }
    }

    private void EnsureActiveAndIdle()
    {
        EnsureActive();
        if (ActiveClips.Any(clip => clip.State is ClipState.Recording or ClipState.Playing or ClipState.Paused))
        {
            throw new DomainException("Stop recording or playback before continuing.");
        }
    }

    private void EnsureAllActiveSegmentsFinalized()
    {
        if (HasUnresolvedSegments)
        {
            throw new DomainException(
                "One or more recording segments have not been safely finalized. Keep this session for recovery or start a new take before exporting.");
        }
    }

    private static string FormatDefaultClipName(string format, int ordinal)
    {
        try
        {
            var result = string.Format(CultureInfo.CurrentCulture, format, ordinal);
            return string.IsNullOrWhiteSpace(result) ? $"Clip {ordinal}" : result.Trim();
        }
        catch (FormatException)
        {
            return $"Clip {ordinal}";
        }
    }
}
