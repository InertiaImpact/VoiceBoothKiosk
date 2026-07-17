using System.Text.Json.Serialization;

namespace VoiceBoothKiosk.Domain;

public sealed class RecordingSegment
{
    [JsonConstructor]
    public RecordingSegment(
        Guid id,
        string relativeWavePath,
        string? relativePeaksPath,
        int order,
        TimeSpan duration,
        AudioFormat format,
        long byteCount,
        string? checksumSha256,
        SegmentState state,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? error)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A segment ID is required.", nameof(id));
        }

        ValidateRelativePath(relativeWavePath, nameof(relativeWavePath));
        if (relativePeaksPath is not null)
        {
            ValidateRelativePath(relativePeaksPath, nameof(relativePeaksPath));
        }

        if (order < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Id = id;
        RelativeWavePath = relativeWavePath;
        RelativePeaksPath = relativePeaksPath;
        Order = order;
        Duration = duration;
        Format = format ?? throw new ArgumentNullException(nameof(format));
        ByteCount = byteCount;
        ChecksumSha256 = checksumSha256;
        State = state;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Error = error;
    }

    public Guid Id { get; private set; }
    public string RelativeWavePath { get; private set; }
    public string? RelativePeaksPath { get; private set; }
    public int Order { get; private set; }
    public TimeSpan Duration { get; private set; }
    public AudioFormat Format { get; private set; }
    public long ByteCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public SegmentState State { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? Error { get; private set; }

    public static RecordingSegment Start(
        Guid id,
        string relativeWavePath,
        string? relativePeaksPath,
        int order,
        AudioFormat format,
        DateTimeOffset startedAtUtc) =>
        new(id, relativeWavePath, relativePeaksPath, order, TimeSpan.Zero, format, 0, null,
            SegmentState.Recording, startedAtUtc, null, null);

    public void Complete(TimeSpan duration, long byteCount, string? checksumSha256, DateTimeOffset completedAtUtc)
    {
        RequireState(SegmentState.Recording);
        SetFinalizedMetadata(duration, byteCount, checksumSha256, completedAtUtc);
    }

    public void CompleteRecovered(TimeSpan duration, long byteCount, string? checksumSha256, DateTimeOffset completedAtUtc)
    {
        RequireState(SegmentState.Recoverable);
        SetFinalizedMetadata(duration, byteCount, checksumSha256, completedAtUtc);
    }

    private void SetFinalizedMetadata(TimeSpan duration, long byteCount, string? checksumSha256, DateTimeOffset completedAtUtc)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Duration = duration;
        ByteCount = byteCount;
        ChecksumSha256 = checksumSha256;
        CompletedAtUtc = completedAtUtc;
        State = SegmentState.Finalized;
        Error = null;
    }

    public void MarkRecoverable(string? error = null)
    {
        if (State is not SegmentState.Recording and not SegmentState.Failed)
        {
            throw new DomainException($"Segment {Id} cannot become recoverable from {State}.");
        }

        State = SegmentState.Recoverable;
        Error = error;
    }

    public void MarkFailed(string error)
    {
        if (State is not SegmentState.Recording and not SegmentState.Recoverable)
        {
            throw new DomainException($"Segment {Id} cannot fail from {State}.");
        }

        State = SegmentState.Failed;
        Error = string.IsNullOrWhiteSpace(error) ? "Recording failed." : error.Trim();
    }

    private void RequireState(SegmentState expected)
    {
        if (State != expected)
        {
            throw new DomainException($"Segment {Id} must be {expected}; it is {State}.");
        }
    }

    private static void ValidateRelativePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (Path.IsPathRooted(path) || path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains(".."))
        {
            throw new ArgumentException("Working paths must be relative and cannot traverse parent directories.", parameterName);
        }
    }
}
