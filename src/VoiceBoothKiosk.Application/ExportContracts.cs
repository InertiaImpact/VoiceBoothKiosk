using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Application;

public sealed record DestinationValidationResult
{
    public required bool IsValid { get; init; }
    public required string Message { get; init; }
    public long? AvailableBytes { get; init; }
}

public sealed record ClipExportSource
{
    public required Guid ClipId { get; init; }
    public required string OutputFileName { get; init; }
    public required IReadOnlyList<PlaybackSegmentSource> Segments { get; init; }
    public required TimeSpan Duration { get; init; }
}

public sealed record ExportRequest
{
    public required Guid SessionId { get; init; }
    public required string SessionDisplayName { get; init; }
    public required DestinationPreset Destination { get; init; }
    public required string SessionFolderName { get; init; }
    public required IReadOnlyList<ClipExportSource> Clips { get; init; }
    public required AudioFormat Format { get; init; }
    public required int Mp3BitrateKbps { get; init; }
}

public sealed record ExportProgress
{
    public required ExportState State { get; init; }
    public required double FractionComplete { get; init; }
    public Guid? ClipId { get; init; }
    public string? Message { get; init; }
}

public sealed record ExportResult
{
    public required string SessionFolderPath { get; init; }
    public required IReadOnlyList<string> ExportedFilePaths { get; init; }
    public IReadOnlyList<string> RawFilePaths { get; init; } = [];
}

public sealed record ExportEncoderValidationResult(
    bool IsValid,
    string Message,
    IReadOnlyList<int> SupportedBitratesKbps);

/// <summary>Non-mutating startup check for the exact configured encoder profile.</summary>
public interface IExportPreflightService
{
    Task<ExportEncoderValidationResult> ValidateEncoderAsync(
        AudioFormat format,
        int mp3BitrateKbps,
        CancellationToken cancellationToken = default);
}

public interface IExportService
{
    Task<DestinationValidationResult> ValidateDestinationAsync(DestinationPreset destination, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<string>> GetExistingSessionFolderNamesAsync(DestinationPreset destination, CancellationToken cancellationToken = default);
    Task<ExportResult?> FindCommittedExportAsync(
        Guid sessionId,
        IReadOnlyCollection<Guid> finalizedSegmentIds,
        IReadOnlyList<DestinationPreset> destinations,
        CancellationToken cancellationToken = default) => Task.FromResult<ExportResult?>(null);
    Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default);
}
