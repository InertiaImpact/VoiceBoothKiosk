using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Persistence;

internal sealed record TrashFileEntry(string OriginalRelativePath, string TrashRelativePath);

internal sealed record TrashManifest
{
    public required TrashReceipt Receipt { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required IReadOnlyList<TrashFileEntry> Entries { get; init; }
}

public sealed record TrashReconciliationResult(
    int RestoredFileCount,
    int CompletedReceiptCount,
    IReadOnlyList<string> Errors);

/// <summary>
/// Rolls back a trash move when the durable session manifest still references those files.
/// A move is journaled in trash.json before the first WAV is moved, so reconciliation is
/// restartable even if this process is itself interrupted midway.
/// </summary>
public sealed class TrashRecoveryReconciler
{
    private readonly ApplicationPaths _paths;
    private readonly AtomicJsonFile _json;

    public TrashRecoveryReconciler(ApplicationPaths paths, AtomicJsonFile? json = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _json = json ?? new AtomicJsonFile();
    }

    public async Task<TrashReconciliationResult> ReconcileAsync(
        RecordingSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sessionRoot = _paths.GetSessionDirectory(session.Id);
        var trashRoot = _paths.GetTrashDirectory(session.Id);
        if (!Directory.Exists(trashRoot)) return new(0, 0, []);

        // Deleted clips are intentionally kept in trash after their new manifest commits.
        // An older pre-change manifest still sees the clip as active, which is the signal
        // that the filesystem move must be rolled back.
        var referencedPaths = BuildReferencedPaths(sessionRoot, session);
        var restored = 0;
        var completedReceipts = 0;
        var errors = new List<string>();

        foreach (var receiptRoot in Directory.EnumerateDirectories(trashRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(receiptRoot, "trash.json");
            if (!File.Exists(manifestPath) && !File.Exists(AtomicJsonFile.GetBackupPath(manifestPath)))
            {
                continue;
            }

            TrashManifest manifest;
            try
            {
                manifest = await _json.ReadAsync<TrashManifest>(manifestPath, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Trash receipt manifest is empty.");
                ValidateManifest(session, receiptRoot, manifest);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(receiptRoot)}: {exception.Message}");
                continue;
            }

            var referencedEntries = new List<TrashFileEntry>();
            try
            {
                foreach (var entry in manifest.Entries)
                {
                    var destination = RecoveryDiscovery.ResolveContainedPath(sessionRoot, entry.OriginalRelativePath);
                    _ = RecoveryDiscovery.ResolveContainedPath(receiptRoot, entry.TrashRelativePath);
                    if (referencedPaths.Contains(destination)) referencedEntries.Add(entry);
                }
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"{manifest.Receipt.Id}: {exception.Message}");
                continue;
            }

            if (referencedEntries.Count == 0) continue;

            var movedThisAttempt = new List<TrashFileEntry>();
            try
            {
                foreach (var entry in referencedEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = RecoveryDiscovery.ResolveContainedPath(receiptRoot, entry.TrashRelativePath);
                    var destination = RecoveryDiscovery.ResolveContainedPath(sessionRoot, entry.OriginalRelativePath);
                    var sourceExists = File.Exists(source);
                    var destinationExists = File.Exists(destination);

                    if (sourceExists && destinationExists)
                    {
                        throw new IOException($"Both working and trash copies exist for '{entry.OriginalRelativePath}'; neither was overwritten.");
                    }
                    if (!sourceExists && !destinationExists)
                    {
                        throw new FileNotFoundException($"Neither working nor trash copy exists for '{entry.OriginalRelativePath}'.");
                    }
                    if (!sourceExists)
                    {
                        continue; // Already restored by an interrupted earlier reconciliation.
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(source, destination);
                    movedThisAttempt.Add(entry);
                    restored++;
                }

                var allReceiptEntriesWereReferenced = manifest.Entries.Count == referencedEntries.Count;
                var allReferencedFilesRestored = referencedEntries.All(entry =>
                    File.Exists(RecoveryDiscovery.ResolveContainedPath(sessionRoot, entry.OriginalRelativePath)));
                if (allReceiptEntriesWereReferenced && allReferencedFilesRestored)
                {
                    Directory.Delete(receiptRoot, recursive: true);
                    completedReceipts++;
                }
            }
            catch (OperationCanceledException)
            {
                RollBackCurrentAttempt(sessionRoot, receiptRoot, movedThisAttempt);
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                RollBackCurrentAttempt(sessionRoot, receiptRoot, movedThisAttempt);
                restored -= movedThisAttempt.Count;
                errors.Add($"{manifest.Receipt.Id}: {exception.Message}");
            }
        }

        return new(restored, completedReceipts, errors);
    }

    private static HashSet<string> BuildReferencedPaths(string sessionRoot, RecordingSession session)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in session.ActiveClips.SelectMany(clip => clip.Segments))
        {
            paths.Add(RecoveryDiscovery.ResolveContainedPath(sessionRoot, segment.RelativeWavePath));
            if (segment.State is not SegmentState.Finalized)
                paths.Add(RecoveryDiscovery.ResolveContainedPath(sessionRoot, $"{segment.RelativeWavePath}.partial"));
            if (segment.RelativePeaksPath is not null)
                paths.Add(RecoveryDiscovery.ResolveContainedPath(sessionRoot, segment.RelativePeaksPath));
        }
        return paths;
    }

    private static void ValidateManifest(RecordingSession session, string receiptRoot, TrashManifest manifest)
    {
        if (manifest.Receipt.SessionId != session.Id)
            throw new InvalidDataException("Trash receipt belongs to a different session.");
        if (manifest.Receipt.Id == Guid.Empty || manifest.Receipt.ClipId == Guid.Empty)
            throw new InvalidDataException("Trash receipt identifiers are invalid.");
        if (!string.Equals(Path.GetFileName(receiptRoot), manifest.Receipt.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Trash receipt ID does not match its directory.");
        if (manifest.Entries is null)
            throw new InvalidDataException("Trash receipt has no file-entry collection.");
    }

    private static void RollBackCurrentAttempt(
        string sessionRoot,
        string receiptRoot,
        IReadOnlyList<TrashFileEntry> movedEntries)
    {
        foreach (var entry in movedEntries.Reverse())
        {
            try
            {
                var source = RecoveryDiscovery.ResolveContainedPath(sessionRoot, entry.OriginalRelativePath);
                var destination = RecoveryDiscovery.ResolveContainedPath(receiptRoot, entry.TrashRelativePath);
                if (!File.Exists(source) || File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // The receipt journal remains. A later restart will finish whichever side exists.
            }
        }
    }
}
