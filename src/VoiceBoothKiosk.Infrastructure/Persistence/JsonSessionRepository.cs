using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Persistence;

public sealed class JsonSessionRepository : ISessionRepository
{
    private readonly ApplicationPaths _paths;
    private readonly AtomicJsonFile _json;
    private readonly RecoveryDiscovery _discovery;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSessionRepository(ApplicationPaths paths, AtomicJsonFile? json = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _json = json ?? new AtomicJsonFile();
        _discovery = new RecoveryDiscovery(paths, _json);
    }

    public async Task SaveAsync(RecordingSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        _paths.EnsureLocalDirectories();
        Directory.CreateDirectory(_paths.GetSessionDirectory(session.Id));
        var manifest = new SessionManifest
        {
            ApplicationVersion = session.SettingsSnapshot.ApplicationVersion,
            SavedAtUtc = DateTimeOffset.UtcNow,
            Session = session
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _json.WriteAsync(_paths.GetSessionManifestPath(session.Id), manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordingSession?> LoadActiveAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Where(candidate => candidate.Status == RecoveryCandidateStatus.Valid && candidate.Session is not null)
            .Select(candidate => candidate.Session!)
            // A coordinator changes an interrupted Active/Exporting session to Recoverable and saves it.
            // Recoverable must remain part of the single current-session lookup on every later restart.
            .Where(session => session.State is SessionState.Active or SessionState.Creating or SessionState.Exporting or SessionState.Recoverable)
            .OrderByDescending(session => session.StartedAtUtc)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<RecordingSession>> GetRecoverableAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Where(candidate => candidate.Session is not null
                && candidate.Status is RecoveryCandidateStatus.Valid or RecoveryCandidateStatus.MissingSegmentFiles)
            .Select(candidate => candidate.Session!)
            .Where(session => session.State is not (SessionState.Completed or SessionState.Quarantined))
            .OrderByDescending(session => session.StartedAtUtc)
            .ToArray();
    }

    public async Task<IReadOnlyList<RecordingSession>> GetCompletedAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Where(candidate => candidate.Session is not null
                && candidate.Status is RecoveryCandidateStatus.Valid or RecoveryCandidateStatus.MissingSegmentFiles)
            .Select(candidate => candidate.Session!)
            .Where(session => session.State == SessionState.Completed)
            .OrderBy(session => session.CompletedAtUtc ?? session.StartedAtUtc)
            .ToArray();
    }

    public async Task QuarantineAsync(RecordingSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.State != SessionState.Quarantined) session.Quarantine();
        await SaveAsync(session, cancellationToken).ConfigureAwait(false);
        await MoveToQuarantineAsync(session.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeleteContainedDirectory(_paths.WorkDirectory, _paths.GetSessionDirectory(sessionId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MoveToQuarantineAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _paths.EnsureLocalDirectories();
            var source = _paths.GetSessionDirectory(sessionId);
            if (!Directory.Exists(source)) return;
            var destination = _paths.GetQuarantinedSessionDirectory(sessionId);
            if (Directory.Exists(destination))
                destination = $"{destination}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            Directory.Move(source, destination);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void DeleteContainedDirectory(string root, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(directory));
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Refusing to delete outside the working root.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
