using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Persistence;

public sealed class RecoveryDiscovery
{
    private readonly ApplicationPaths _paths;
    private readonly AtomicJsonFile _json;
    private readonly TrashRecoveryReconciler _trashReconciler;
    private readonly WaveRecoveryReconciler _waveReconciler = new();

    public RecoveryDiscovery(ApplicationPaths paths, AtomicJsonFile? json = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _json = json ?? new AtomicJsonFile();
        _trashReconciler = new TrashRecoveryReconciler(paths, _json);
    }

    public async Task<IReadOnlyList<RecoveryCandidate>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureLocalDirectories();
        var results = new List<RecoveryCandidate>();

        foreach (var directory in Directory.EnumerateDirectories(_paths.WorkDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "session.json");
            var partials = EnumerateActivePartialWaveFiles(directory);
            var parsedId = Guid.TryParse(Path.GetFileName(directory), out var id) ? id : (Guid?)null;

            if (!File.Exists(manifestPath) && !File.Exists(AtomicJsonFile.GetBackupPath(manifestPath)))
            {
                results.Add(new(directory, parsedId, null, RecoveryCandidateStatus.MissingManifest, partials, [],
                    "The working folder has no session manifest."));
                continue;
            }

            SessionManifest? manifest;
            try
            {
                manifest = await _json.ReadAsync<SessionManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
                if (manifest is null) throw new InvalidDataException("The session manifest is empty.");
                if (manifest.SchemaVersion > SessionManifest.CurrentSchemaVersion)
                    throw new InvalidDataException($"Session schema {manifest.SchemaVersion} is not supported.");
                if (parsedId.HasValue && manifest.Session.Id != parsedId.Value)
                    throw new InvalidDataException("Session ID does not match its working folder.");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                results.Add(new(directory, parsedId, null, RecoveryCandidateStatus.InvalidManifest, partials, [], exception.Message));
                continue;
            }

            var reconciliation = await _trashReconciler.ReconcileAsync(manifest.Session, cancellationToken).ConfigureAwait(false);
            var waveRecovery = await _waveReconciler.ReconcileAsync(directory, manifest.Session, cancellationToken).ConfigureAwait(false);
            if (waveRecovery.RecoveredSegmentCount > 0)
            {
                manifest = manifest with { SavedAtUtc = DateTimeOffset.UtcNow };
                await _json.WriteAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            }
            partials = EnumerateActivePartialWaveFiles(directory);
            var missing = FindMissingFinalizedFiles(directory, manifest.Session);
            var status = missing.Count == 0 ? RecoveryCandidateStatus.Valid : RecoveryCandidateStatus.MissingSegmentFiles;
            results.Add(new(directory, manifest.Session.Id, manifest.Session, status, partials, missing,
                BuildRecoveryMessage(missing, reconciliation, waveRecovery)));
        }

        return results;
    }

    private static IReadOnlyList<string> FindMissingFinalizedFiles(string sessionDirectory, RecordingSession session)
    {
        var missing = new List<string>();
        foreach (var segment in session.ActiveClips.SelectMany(clip => clip.Segments)
                     .Where(segment => segment.State == SegmentState.Finalized))
        {
            var candidate = ResolveContainedPath(sessionDirectory, segment.RelativeWavePath);
            if (!File.Exists(candidate)) missing.Add(segment.RelativeWavePath);
        }
        return missing;
    }

    private static IReadOnlyList<string> EnumerateActivePartialWaveFiles(string sessionDirectory)
    {
        var segmentsDirectory = Path.Combine(sessionDirectory, "segments");
        return Directory.Exists(segmentsDirectory)
            ? Directory.EnumerateFiles(segmentsDirectory, "*.wav.partial", SearchOption.AllDirectories).ToArray()
            : [];
    }

    private static string? BuildRecoveryMessage(
        IReadOnlyList<string> missing,
        TrashReconciliationResult reconciliation,
        WaveRecoveryResult waveRecovery)
    {
        var messages = new List<string>();
        if (reconciliation.RestoredFileCount > 0)
            messages.Add($"Restored {reconciliation.RestoredFileCount} file(s) from an interrupted trash operation.");
        messages.AddRange(reconciliation.Errors);
        if (waveRecovery.RecoveredSegmentCount > 0)
            messages.Add($"Recovered {waveRecovery.RecoveredSegmentCount} interrupted recording segment(s).");
        messages.AddRange(waveRecovery.Errors);
        if (missing.Count > 0) messages.Add("One or more finalized audio files are missing.");
        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    internal static string ResolveContainedPath(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var relative = Path.GetRelativePath(normalizedRoot, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new InvalidDataException("A manifest path escapes the working session folder.");
        return candidate;
    }
}
