using System.Text.Json.Serialization;

namespace VoiceBoothKiosk.Domain;

public sealed class RecordingClip
{
    private readonly List<RecordingSegment> _segments;

    [JsonConstructor]
    public RecordingClip(
        Guid id,
        int ordinal,
        string displayName,
        bool isCustomNamed,
        ClipState state,
        IReadOnlyList<RecordingSegment>? segments,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? deletedAtUtc,
        ClipState? stateBeforeDelete,
        string? error)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A clip ID is required.", nameof(id));
        }

        if (ordinal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Id = id;
        Ordinal = ordinal;
        DisplayName = displayName.Trim();
        IsCustomNamed = isCustomNamed;
        State = state;
        _segments = segments?.OrderBy(segment => segment.Order).ToList() ?? [];
        CreatedAtUtc = createdAtUtc;
        DeletedAtUtc = deletedAtUtc;
        StateBeforeDelete = stateBeforeDelete;
        Error = error;
    }

    public Guid Id { get; private set; }
    public int Ordinal { get; private set; }
    public string DisplayName { get; private set; }
    public bool IsCustomNamed { get; private set; }
    public ClipState State { get; private set; }
    public IReadOnlyList<RecordingSegment> Segments => _segments;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public ClipState? StateBeforeDelete { get; private set; }
    public string? Error { get; private set; }
    public TimeSpan Duration => TimeSpan.FromTicks(_segments
        .Where(segment => segment.State == SegmentState.Finalized)
        .Sum(segment => segment.Duration.Ticks));
    public bool HasAudio => _segments.Any(segment => segment.State == SegmentState.Finalized);
    public bool IsDeleted => State == ClipState.Deleted;

    public static RecordingClip Create(Guid id, int ordinal, string defaultName, DateTimeOffset createdAtUtc) =>
        new(id, ordinal, defaultName, false, ClipState.Empty, [], createdAtUtc, null, null, null);

    public void Rename(string? displayName, string defaultName)
    {
        EnsureEditable();
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = defaultName.Trim();
            IsCustomNamed = false;
            return;
        }

        DisplayName = displayName.Trim();
        IsCustomNamed = !string.Equals(DisplayName, defaultName.Trim(), StringComparison.Ordinal);
    }

    public RecordingSegment BeginSegment(
        Guid segmentId,
        string relativeWavePath,
        string? relativePeaksPath,
        AudioFormat format,
        DateTimeOffset startedAtUtc)
    {
        EnsureEditable();
        if (State is ClipState.Playing or ClipState.Paused)
        {
            throw new DomainException("Stop playback before recording.");
        }

        if (_segments.Any(segment => segment.State == SegmentState.Recording))
        {
            throw new DomainException("This clip already has an active recording segment.");
        }

        var order = _segments.Count == 0 ? 1 : _segments.Max(segment => segment.Order) + 1;
        var segment = RecordingSegment.Start(segmentId, relativeWavePath, relativePeaksPath, order, format, startedAtUtc);
        _segments.Add(segment);
        State = ClipState.Recording;
        Error = null;
        return segment;
    }

    public void CompleteSegment(Guid segmentId, TimeSpan duration, long byteCount, string? checksumSha256, DateTimeOffset completedAtUtc)
    {
        var segment = RequireSegment(segmentId);
        segment.Complete(duration, byteCount, checksumSha256, completedAtUtc);
        State = HasAudio ? ClipState.Stopped : ClipState.Empty;
        Error = null;
    }

    public void MarkActiveSegmentRecoverable(string? error)
    {
        var segment = _segments.SingleOrDefault(item => item.State == SegmentState.Recording)
            ?? throw new DomainException("This clip has no active segment.");
        segment.MarkRecoverable(error);
        State = ClipState.Error;
        Error = error;
    }

    public void MarkSegmentFailed(Guid segmentId, string error)
    {
        RequireSegment(segmentId).MarkFailed(error);
        State = ClipState.Error;
        Error = error;
    }

    public void CompleteRecoveredSegment(Guid segmentId, TimeSpan duration, long byteCount, string? checksumSha256, DateTimeOffset completedAtUtc)
    {
        EnsureEditable();
        RequireSegment(segmentId).CompleteRecovered(duration, byteCount, checksumSha256, completedAtUtc);
        State = _segments.Any(segment => segment.State is SegmentState.Recoverable or SegmentState.Failed)
            ? ClipState.Error
            : HasAudio ? ClipState.Stopped : ClipState.Empty;
        Error = State == ClipState.Error ? Error : null;
    }

    public void BeginPlayback()
    {
        EnsureEditable();
        if (!HasAudio)
        {
            throw new DomainException("An empty clip cannot be played.");
        }

        if (State == ClipState.Recording)
        {
            throw new DomainException("Stop recording before playback.");
        }

        State = ClipState.Playing;
    }

    public void PausePlayback()
    {
        RequireState(ClipState.Playing);
        State = ClipState.Paused;
    }

    public void ResumePlayback()
    {
        RequireState(ClipState.Paused);
        State = ClipState.Playing;
    }

    public void StopPlayback()
    {
        if (State is not ClipState.Playing and not ClipState.Paused)
        {
            throw new DomainException("This clip is not playing.");
        }

        State = HasAudio ? ClipState.Stopped : ClipState.Empty;
    }

    public IReadOnlyList<RecordingSegment> ResetTake()
    {
        EnsureEditable();
        EnsureIdle();
        var removed = _segments.ToArray();
        _segments.Clear();
        State = ClipState.Empty;
        Error = null;
        return removed;
    }

    public void RestoreTake(IEnumerable<RecordingSegment> segments)
    {
        EnsureEditable();
        EnsureIdle();
        if (_segments.Count != 0)
        {
            throw new DomainException("A take can only be restored into an empty clip.");
        }

        ArgumentNullException.ThrowIfNull(segments);
        _segments.AddRange(segments.OrderBy(segment => segment.Order));
        State = HasAudio ? ClipState.Stopped : ClipState.Empty;
    }

    public void MarkDeleted(DateTimeOffset deletedAtUtc)
    {
        EnsureEditable();
        EnsureIdle();
        StateBeforeDelete = HasAudio ? ClipState.Stopped : ClipState.Empty;
        State = ClipState.Deleted;
        DeletedAtUtc = deletedAtUtc;
    }

    public void RestoreDeleted()
    {
        RequireState(ClipState.Deleted);
        State = StateBeforeDelete is ClipState.Stopped ? ClipState.Stopped : HasAudio ? ClipState.Stopped : ClipState.Empty;
        StateBeforeDelete = null;
        DeletedAtUtc = null;
    }

    public void NormalizeAfterRecovery()
    {
        if (State == ClipState.Deleted)
        {
            return;
        }

        foreach (var segment in _segments.Where(segment => segment.State == SegmentState.Recording))
        {
            segment.MarkRecoverable("Recording was interrupted before the segment was finalized.");
        }

        State = _segments.Any(segment => segment.State is SegmentState.Recoverable or SegmentState.Failed)
            ? ClipState.Error
            : HasAudio ? ClipState.Stopped : ClipState.Empty;
    }

    private RecordingSegment RequireSegment(Guid id) =>
        _segments.SingleOrDefault(segment => segment.Id == id)
        ?? throw new DomainException($"Segment {id} does not belong to clip {Id}.");

    private void EnsureEditable()
    {
        if (State == ClipState.Deleted)
        {
            throw new DomainException("A deleted clip cannot be changed.");
        }
    }

    private void EnsureIdle()
    {
        if (State is ClipState.Recording or ClipState.Playing or ClipState.Paused)
        {
            throw new DomainException("Stop recording or playback before changing this clip.");
        }
    }

    private void RequireState(ClipState expected)
    {
        if (State != expected)
        {
            throw new DomainException($"Clip {Id} must be {expected}; it is {State}.");
        }
    }
}
