using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Persistence;

namespace VoiceBoothKiosk.Infrastructure.Storage;

public sealed class FileWorkingStorage : IWorkingStorage
{
    private readonly ApplicationPaths _paths;
    private readonly AtomicJsonFile _json;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileWorkingStorage(ApplicationPaths paths, AtomicJsonFile? json = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _json = json ?? new AtomicJsonFile();
    }

    public Task InitializeSessionAsync(RecordingSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        _paths.EnsureLocalDirectories();
        Directory.CreateDirectory(_paths.GetSessionDirectory(session.Id));
        Directory.CreateDirectory(Path.Combine(_paths.GetSessionDirectory(session.Id), "segments"));
        Directory.CreateDirectory(Path.Combine(_paths.GetSessionDirectory(session.Id), "peaks"));
        Directory.CreateDirectory(_paths.GetTrashDirectory(session.Id));
        return Task.CompletedTask;
    }

    public async Task<WorkingSegmentAllocation> AllocateSegmentAsync(
        Guid sessionId,
        Guid clipId,
        Guid segmentId,
        CancellationToken cancellationToken = default)
    {
        RequireIds(sessionId, clipId, segmentId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessionDirectory = _paths.GetSessionDirectory(sessionId);
            if (!Directory.Exists(sessionDirectory))
                throw new DirectoryNotFoundException("The session working folder has not been initialized.");
            var segmentDirectory = _paths.GetClipSegmentDirectory(sessionId, clipId);
            var peaksDirectory = Path.Combine(sessionDirectory, "peaks");
            Directory.CreateDirectory(segmentDirectory);
            Directory.CreateDirectory(peaksDirectory);

            var finalWave = _paths.GetSegmentPath(sessionId, clipId, segmentId);
            var partialWave = _paths.GetSegmentPath(sessionId, clipId, segmentId, partial: true);
            var peaks = _paths.GetPeaksPath(sessionId, segmentId);
            if (File.Exists(finalWave) || File.Exists(partialWave) || File.Exists(peaks))
                throw new IOException($"Working files for segment {segmentId} already exist.");

            return new WorkingSegmentAllocation
            {
                SegmentId = segmentId,
                RelativeFinalWavePath = Path.GetRelativePath(sessionDirectory, finalWave),
                RelativePeakCachePath = Path.GetRelativePath(sessionDirectory, peaks),
                AbsolutePartialWavePath = partialWave,
                AbsoluteFinalWavePath = finalWave,
                AbsolutePeakCachePath = peaks
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public string GetAbsoluteWavePath(Guid sessionId, RecordingSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return RecoveryDiscovery.ResolveContainedPath(_paths.GetSessionDirectory(sessionId), segment.RelativeWavePath);
    }

    public string? GetAbsolutePeakCachePath(Guid sessionId, RecordingSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return segment.RelativePeaksPath is null
            ? null
            : RecoveryDiscovery.ResolveContainedPath(_paths.GetSessionDirectory(sessionId), segment.RelativePeaksPath);
    }

    public Task<long> GetAvailableBytesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _paths.EnsureLocalDirectories();
        var root = Path.GetPathRoot(_paths.LocalDataRoot)
            ?? throw new IOException("The local working drive could not be determined.");
        return Task.FromResult(new DriveInfo(root).AvailableFreeSpace);
    }

    public async Task<TrashReceipt> MoveSegmentsToTrashAsync(
        Guid sessionId,
        Guid clipId,
        IReadOnlyList<RecordingSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        RequireIds(sessionId, clipId, Guid.NewGuid());
        var receipt = new TrashReceipt
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ClipId = clipId,
            SegmentIds = segments.Select(segment => segment.Id).ToArray()
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessionRoot = _paths.GetSessionDirectory(sessionId);
            var receiptRoot = GetReceiptDirectory(receipt);
            Directory.CreateDirectory(receiptRoot);
            var entries = BuildTrashEntries(sessionRoot, receiptRoot, segments);
            var manifest = new TrashManifest { Receipt = receipt, CreatedAtUtc = DateTimeOffset.UtcNow, Entries = entries };
            await _json.WriteAsync(Path.Combine(receiptRoot, "trash.json"), manifest, cancellationToken).ConfigureAwait(false);

            var moved = new List<TrashFileEntry>();
            try
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = RecoveryDiscovery.ResolveContainedPath(sessionRoot, entry.OriginalRelativePath);
                    var destination = RecoveryDiscovery.ResolveContainedPath(receiptRoot, entry.TrashRelativePath);
                    if (!File.Exists(source)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(source, destination);
                    moved.Add(entry);
                }
            }
            catch
            {
                foreach (var entry in moved.AsEnumerable().Reverse()) MoveBack(sessionRoot, receiptRoot, entry);
                throw;
            }
            return receipt;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreTrashAsync(TrashReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var receiptRoot = GetReceiptDirectory(receipt);
            var manifest = await RequireTrashManifestAsync(receiptRoot, receipt, cancellationToken).ConfigureAwait(false);
            var sessionRoot = _paths.GetSessionDirectory(receipt.SessionId);
            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MoveBack(sessionRoot, receiptRoot, entry);
            }
            if (Directory.Exists(receiptRoot)) Directory.Delete(receiptRoot, recursive: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteTrashAsync(TrashReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteContainedDirectory(_paths.GetTrashDirectory(receipt.SessionId), GetReceiptDirectory(receipt));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task QuarantineAsync(Guid sessionId, CancellationToken cancellationToken = default)
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

    public async Task DeleteCompletedSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteContainedDirectory(_paths.WorkDirectory, _paths.GetSessionDirectory(sessionId));
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<TrashFileEntry> BuildTrashEntries(
        string sessionRoot,
        string receiptRoot,
        IReadOnlyList<RecordingSegment> segments)
    {
        var entries = new List<TrashFileEntry>();
        foreach (var segment in segments)
        {
            Add(segment.RelativeWavePath);
            Add($"{segment.RelativeWavePath}.partial");
            if (segment.RelativePeaksPath is not null) Add(segment.RelativePeaksPath);

            void Add(string relative)
            {
                var source = RecoveryDiscovery.ResolveContainedPath(sessionRoot, relative);
                if (!File.Exists(source)) return;
                var leaf = $"{entries.Count:0000}-{Path.GetFileName(source)}";
                _ = RecoveryDiscovery.ResolveContainedPath(receiptRoot, Path.Combine("files", leaf));
                entries.Add(new(relative, Path.Combine("files", leaf)));
            }
        }
        return entries;
    }

    private async Task<TrashManifest> RequireTrashManifestAsync(
        string receiptRoot,
        TrashReceipt receipt,
        CancellationToken cancellationToken)
    {
        var manifest = await _json.ReadAsync<TrashManifest>(Path.Combine(receiptRoot, "trash.json"), cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The trash receipt manifest is missing.");
        if (manifest.Receipt.Id != receipt.Id || manifest.Receipt.SessionId != receipt.SessionId || manifest.Receipt.ClipId != receipt.ClipId)
            throw new InvalidDataException("The trash receipt does not match its manifest.");
        return manifest;
    }

    private string GetReceiptDirectory(TrashReceipt receipt) =>
        Path.Combine(_paths.GetTrashDirectory(receipt.SessionId), receipt.Id.ToString("D"));

    private static void MoveBack(string sessionRoot, string receiptRoot, TrashFileEntry entry)
    {
        var source = RecoveryDiscovery.ResolveContainedPath(receiptRoot, entry.TrashRelativePath);
        if (!File.Exists(source)) return;
        var destination = RecoveryDiscovery.ResolveContainedPath(sessionRoot, entry.OriginalRelativePath);
        if (File.Exists(destination)) throw new IOException($"Cannot restore '{entry.OriginalRelativePath}' because a file already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
    }

    private static void RequireIds(Guid sessionId, Guid clipId, Guid segmentId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (clipId == Guid.Empty) throw new ArgumentException("A clip ID is required.", nameof(clipId));
        if (segmentId == Guid.Empty) throw new ArgumentException("A segment ID is required.", nameof(segmentId));
    }

    private static void DeleteContainedDirectory(string root, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(directory));
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Refusing to delete outside the expected storage root.");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

}
