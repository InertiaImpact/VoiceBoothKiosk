using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateTimeOffset LocalNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}

public interface ISessionRepository
{
    Task SaveAsync(RecordingSession session, CancellationToken cancellationToken = default);
    Task<RecordingSession?> LoadActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecordingSession>> GetRecoverableAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecordingSession>> GetCompletedAsync(CancellationToken cancellationToken = default);
    Task QuarantineAsync(RecordingSession session, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record WorkingSegmentAllocation
{
    public required Guid SegmentId { get; init; }
    public required string RelativeFinalWavePath { get; init; }
    public string? RelativePeakCachePath { get; init; }
    public required string AbsolutePartialWavePath { get; init; }
    public required string AbsoluteFinalWavePath { get; init; }
    public string? AbsolutePeakCachePath { get; init; }
}

public sealed record TrashReceipt
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required Guid ClipId { get; init; }
    public required IReadOnlyList<Guid> SegmentIds { get; init; }
}

public interface IWorkingStorage
{
    Task InitializeSessionAsync(RecordingSession session, CancellationToken cancellationToken = default);
    Task<WorkingSegmentAllocation> AllocateSegmentAsync(Guid sessionId, Guid clipId, Guid segmentId, CancellationToken cancellationToken = default);
    string GetAbsoluteWavePath(Guid sessionId, RecordingSegment segment);
    string? GetAbsolutePeakCachePath(Guid sessionId, RecordingSegment segment);
    Task<long> GetAvailableBytesAsync(CancellationToken cancellationToken = default);
    Task<TrashReceipt> MoveSegmentsToTrashAsync(Guid sessionId, Guid clipId, IReadOnlyList<RecordingSegment> segments, CancellationToken cancellationToken = default);
    Task RestoreTrashAsync(TrashReceipt receipt, CancellationToken cancellationToken = default);
    Task DeleteTrashAsync(TrashReceipt receipt, CancellationToken cancellationToken = default);
    Task QuarantineAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DeleteCompletedSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record SettingsValidationError(string Path, string Message);
public sealed record SettingsValidationResult(IReadOnlyList<SettingsValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public static SettingsValidationResult Valid { get; } = new([]);
}

public sealed class SettingsChangedEventArgs : EventArgs
{
    public required KioskSettings Settings { get; init; }
}

public interface ISettingsStorage
{
    Task<KioskSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(KioskSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Validated configuration access. Machine-policy implementations may reject SaveAsync for kiosk users.</summary>
public interface IConfigurationService : ISettingsStorage
{
    event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
    SettingsValidationResult Validate(KioskSettings settings);
}
