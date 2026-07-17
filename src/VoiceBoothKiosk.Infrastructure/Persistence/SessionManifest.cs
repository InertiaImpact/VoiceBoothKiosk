using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Infrastructure.Persistence;

public sealed record SessionManifest
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ApplicationVersion { get; init; } = "1.0.0";
    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required RecordingSession Session { get; init; }
}

public enum RecoveryCandidateStatus
{
    Valid,
    MissingManifest,
    InvalidManifest,
    MissingSegmentFiles
}

public sealed record RecoveryCandidate(
    string DirectoryPath,
    Guid? SessionId,
    RecordingSession? Session,
    RecoveryCandidateStatus Status,
    IReadOnlyList<string> PartialWaveFiles,
    IReadOnlyList<string> MissingFiles,
    string? Error);
