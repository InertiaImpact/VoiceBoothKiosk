using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Application;

public enum CoordinatorMode
{
    Uninitialized,
    Welcome,
    RecoveryAvailable,
    Ready,
    Recording,
    Playing,
    Paused,
    Exporting,
    Completed
}

public sealed record CoordinatorCapabilities
{
    public bool CanStartSession { get; init; }
    public bool CanRecord { get; init; }
    public bool CanStop { get; init; }
    public bool CanPlay { get; init; }
    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanSeek { get; init; }
    public bool CanEditClips { get; init; }
    public bool CanNewTake { get; init; }
    public bool CanEndSession { get; init; }
    public bool CanChangeDevices { get; init; }
    public bool CanUndo { get; init; }
    public bool CanCancelExport { get; init; }
}

public sealed record SessionCoordinatorState
{
    public required long Version { get; init; }
    public required CoordinatorMode Mode { get; init; }
    public required KioskSettings Settings { get; init; }
    public RecordingSession? Session { get; init; }
    public RecordingClip? SelectedClip { get; init; }
    public required CoordinatorCapabilities Capabilities { get; init; }
    public string? StatusMessage { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan RecordingPosition { get; init; }
    public TimeSpan PlaybackPosition { get; init; }
    public float InputRmsLevel { get; init; }
    public float InputPeakLevel { get; init; }
    public IReadOnlyList<WaveformPeak> LiveWaveformPeaks { get; init; } = [];
    public ExportProgress? ExportProgress { get; init; }
    public ExportResult? ExportResult { get; init; }
}

public sealed class SessionCoordinatorStateChangedEventArgs : EventArgs
{
    public required SessionCoordinatorState State { get; init; }
}

public sealed class ApplicationOperationFailedEventArgs : EventArgs
{
    public required string Operation { get; init; }
    public required string UserMessage { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Owns the single-session/single-audio-operation state machine. UI code should call this service rather
/// than controlling capture and playback implementations directly.
/// </summary>
public sealed class SessionCoordinator : IAsyncDisposable
{
    private readonly IAudioCaptureService _capture;
    private readonly IAudioPlaybackService _playback;
    private readonly IAudioDeviceService _devices;
    private readonly IWaveformService _waveforms;
    private readonly IExportService _export;
    private readonly ISessionRepository _sessions;
    private readonly IWorkingStorage _workingStorage;
    private readonly IConfigurationService _configuration;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();

    private KioskSettings _settings = KioskSettings.CreateDefault();
    private RecordingSession? _session;
    private CoordinatorMode _mode = CoordinatorMode.Uninitialized;
    private string? _statusMessage;
    private string? _errorMessage;
    private TimeSpan _recordingPosition;
    private TimeSpan _playbackPosition;
    private float _inputRms;
    private float _inputPeak;
    private IReadOnlyList<WaveformPeak> _livePeaks = [];
    private ExportProgress? _exportProgress;
    private ExportResult? _exportResult;
    private WorkingSegmentAllocation? _activeAllocation;
    private Guid? _activeClipId;
    private PendingUndo? _pendingUndo;
    private CancellationTokenSource? _exportCancellation;
    private bool _completionManifestPersisted;
    private long _version;
    private bool _disposed;

    public SessionCoordinator(
        IAudioCaptureService capture,
        IAudioPlaybackService playback,
        IAudioDeviceService devices,
        IWaveformService waveforms,
        IExportService export,
        ISessionRepository sessions,
        IWorkingStorage workingStorage,
        IConfigurationService configuration,
        IClock clock)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _waveforms = waveforms ?? throw new ArgumentNullException(nameof(waveforms));
        _export = export ?? throw new ArgumentNullException(nameof(export));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _workingStorage = workingStorage ?? throw new ArgumentNullException(nameof(workingStorage));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _capture.ProgressChanged += OnCaptureProgressChanged;
        _capture.Faulted += OnCaptureFaulted;
        _playback.ProgressChanged += OnPlaybackProgressChanged;
        _playback.StateChanged += OnPlaybackStateChanged;
        _configuration.SettingsChanged += OnSettingsChanged;
    }

    public event EventHandler<SessionCoordinatorStateChangedEventArgs>? StateChanged;
    public event EventHandler<ApplicationOperationFailedEventArgs>? OperationFailed;

    public SessionCoordinatorState State
    {
        get
        {
            lock (_stateGate)
            {
                return CreateState();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
            var validation = _configuration.Validate(_settings);
            if (!validation.IsValid)
            {
                throw new InvalidDataException($"Configuration is invalid: {string.Join("; ", validation.Errors.Select(error => $"{error.Path}: {error.Message}"))}");
            }

            var cleanupErrors = await CleanupExpiredCompletedSessionsAsync(cancellationToken).ConfigureAwait(false);

            _session = await _sessions.LoadActiveAsync(cancellationToken).ConfigureAwait(false);
            if (_session is null)
            {
                // Some repositories distinguish "active" from already-recoverable manifests.
                // Falling back here keeps recovery available across any number of restarts.
                var recoverable = await _sessions.GetRecoverableAsync(cancellationToken).ConfigureAwait(false);
                _session = recoverable
                    .Where(session => session.State is not (SessionState.Completed or SessionState.Quarantined))
                    .OrderByDescending(session => session.StartedAtUtc)
                    .FirstOrDefault();
            }

            if (_session is not null &&
                _session.State is SessionState.Exporting or SessionState.Recoverable &&
                !_session.ActiveClips.SelectMany(static clip => clip.Segments)
                    .Any(static segment => segment.State != SegmentState.Finalized))
            {
                var committed = await _export.FindCommittedExportAsync(
                    _session.Id,
                    GetFinalizedSegmentIds(_session),
                    _settings.Destinations,
                    cancellationToken).ConfigureAwait(false);
                if (committed is not null)
                {
                    ClearTransientState();
                    _session.CompleteRecoveredExport(_clock.UtcNow);
                    _exportResult = committed;
                    _exportProgress = new()
                    {
                        State = ExportState.Completed,
                        FractionComplete = 1,
                        Message = "Recovered a completed destination export."
                    };
                    _mode = CoordinatorMode.Completed;
                    _statusMessage = $"Recovered {committed.ExportedFilePaths.Count} already-saved clip(s).";
                    try
                    {
                        await _sessions.SaveAsync(_session, CancellationToken.None).ConfigureAwait(false);
                        _completionManifestPersisted = true;
                    }
                    catch (Exception persistenceException)
                    {
                        _errorMessage = "The saved audio was recovered, but local completion status could not be recorded. Press Done to finish cleanup.";
                        OperationFailed?.Invoke(this, new()
                        {
                            Operation = "Recover committed export",
                            UserMessage = _errorMessage,
                            Exception = persistenceException
                        });
                    }

                    if (cleanupErrors.Count > 0)
                    {
                        _errorMessage ??= "Some expired local session files could not be cleaned up; cleanup will be retried at next startup.";
                    }
                    PublishState();
                    return;
                }
            }

            if (_session is not null && _session.State is SessionState.Active or SessionState.Creating or SessionState.Exporting)
            {
                _session.MarkRecoverable("The application stopped before the session was completed.");
                await _sessions.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
            }

            if (_session is not null && _session.State != SessionState.Recoverable)
            {
                // A repository should not return completed/quarantined records as current work.
                _session = null;
            }

            _mode = _session?.State == SessionState.Recoverable
                ? CoordinatorMode.RecoveryAvailable
                : CoordinatorMode.Welcome;
            _statusMessage = _mode == CoordinatorMode.RecoveryAvailable
                ? "A recording session was interrupted and can be resumed."
                : "Ready.";
            ClearTransientState();
            if (cleanupErrors.Count > 0)
            {
                _errorMessage = "Some expired local session files could not be cleaned up; cleanup will be retried at next startup.";
                OperationFailed?.Invoke(this, new()
                {
                    Operation = "Completed-session retention cleanup",
                    UserMessage = _errorMessage,
                    Exception = new AggregateException(cleanupErrors)
                });
            }
            PublishState();
        }
        catch (Exception exception)
        {
            ReportFailure("Initialize", "Voice Booth could not finish startup.", exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var isWelcome = _mode == CoordinatorMode.Welcome && _session is null;
            var isStoppedSession = _mode == CoordinatorMode.Ready && _session?.State == SessionState.Active;
            if (!isWelcome && !isStoppedSession)
            {
                throw new DomainException("Settings can be changed only from the welcome screen or while a session is stopped.");
            }

            var settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
            var validation = _configuration.Validate(settings);
            if (!validation.IsValid)
            {
                throw new InvalidDataException(
                    $"Configuration is invalid: {string.Join("; ", validation.Errors.Select(error => $"{error.Path}: {error.Message}"))}");
            }

            _settings = settings;
            _statusMessage = "Settings reloaded.";
            _errorMessage = null;
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StartSessionAsync(string displayName, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Welcome);
            var session = RecordingSession.Create(displayName, _clock.LocalNow, _clock.UtcNow, SessionSettingsSnapshot.From(_settings));
            await _workingStorage.InitializeSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            _session = session;
            _completionManifestPersisted = false;
            _mode = CoordinatorMode.Ready;
            _statusMessage = "Press Record when you are ready.";
            _errorMessage = null;
            _exportResult = null;
            PublishState();
        }
        catch (Exception exception)
        {
            ReportFailure("Start session", "The session could not be started.", exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ResumeRecoveredSessionAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.RecoveryAvailable);
            var session = RequireSession();
            session.ResumeRecovered();
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            _mode = CoordinatorMode.Ready;
            _statusMessage = "The interrupted session was recovered.";
            _errorMessage = null;
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task AbandonRecoveredSessionAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.RecoveryAvailable);
            var session = RequireSession();
            session.Quarantine();
            await _sessions.QuarantineAsync(session, cancellationToken).ConfigureAwait(false);
            await _workingStorage.QuarantineAsync(session.Id, cancellationToken).ConfigureAwait(false);
            _session = null;
            _mode = CoordinatorMode.Welcome;
            _statusMessage = "Ready.";
            _errorMessage = null;
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Safely hands an abandoned active session to administrator recovery without deleting its audio.
    /// This is intended for a confirmed kiosk inactivity/privacy handoff.
    /// </summary>
    public async Task QuarantineActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = RequireActiveSession();
            var errors = new List<Exception>();

            if (_mode == CoordinatorMode.Recording)
            {
                try
                {
                    await StopRecordingCoreAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // StopRecordingCore marks the unfinished segment recoverable before throwing.
                    errors.Add(exception);
                }
            }
            else if (_mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
            {
                try
                {
                    await StopPlaybackCoreAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Playback owns no unsaved samples, so quarantine can continue safely.
                    errors.Add(exception);
                }
            }
            else
            {
                RequireMode(CoordinatorMode.Ready);
            }

            session.Quarantine();
            try
            {
                await _sessions.QuarantineAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            try
            {
                await _workingStorage.QuarantineAsync(session.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            // Privacy handoff is local-UI authoritative even when storage needs administrator attention.
            // Both failure paths retain rather than delete the working directory.
            _session = null;
            _mode = CoordinatorMode.Welcome;
            ClearTransientState();
            _statusMessage = "Ready.";
            if (errors.Count == 0)
            {
                PublishState();
            }
            else
            {
                ReportNonFatalPersistenceFailure(
                    "Quarantine active session",
                    "The previous session was hidden from the kiosk, but administrator recovery needs attention.",
                    new AggregateException(errors));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StartRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            var session = RequireActiveSession();
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            var clip = session.SelectedClip;
            var segmentId = Guid.NewGuid();
            var allocation = await _workingStorage.AllocateSegmentAsync(session.Id, clip.Id, segmentId, cancellationToken).ConfigureAwait(false);
            session.BeginSegment(clip.Id, segmentId, allocation.RelativeFinalWavePath, allocation.RelativePeakCachePath, _clock.UtcNow);
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            var request = new AudioCaptureRequest
            {
                SessionId = session.Id,
                ClipId = clip.Id,
                SegmentId = segmentId,
                PartialWavePath = allocation.AbsolutePartialWavePath,
                FinalWavePath = allocation.AbsoluteFinalWavePath,
                PeakCachePath = allocation.AbsolutePeakCachePath,
                InputDevice = session.SettingsSnapshot.InputDevice,
                Format = session.SettingsSnapshot.RecordingFormat
            };
            try
            {
                await _capture.StartAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                session.FailSegment(clip.Id, segmentId, exception.Message);
                await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
                throw;
            }

            _activeAllocation = allocation;
            _activeClipId = clip.Id;
            _mode = CoordinatorMode.Recording;
            _recordingPosition = TimeSpan.Zero;
            _inputRms = 0;
            _inputPeak = 0;
            _livePeaks = [];
            _errorMessage = null;
            _statusMessage = clip.HasAudio ? "Continuing recording…" : "Recording…";
            PublishState();
        }
        catch (Exception exception)
        {
            ReportFailure("Record", "Recording could not be started.", exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Recording);
            await StopRecordingCoreAsync(cancellationToken).ConfigureAwait(false);
            PublishState();
        }
        catch (Exception exception)
        {
            ReportFailure("Stop recording", "The recording could not be finalized. Recovery data has been kept.", exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task PlaySelectedAsync(TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            var session = RequireActiveSession();
            var clip = session.SelectedClip;
            var segments = BuildPlaybackSources(session, clip);
            session.BeginPlayback(clip.Id);
            try
            {
                await _playback.PlayAsync(new AudioPlaybackRequest
                {
                    ClipId = clip.Id,
                    Segments = segments,
                    OutputDevice = session.SettingsSnapshot.OutputDevice,
                    StartPosition = startPosition ?? TimeSpan.Zero
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                session.StopPlayback();
                throw;
            }

            _mode = CoordinatorMode.Playing;
            _playbackPosition = startPosition ?? TimeSpan.Zero;
            _statusMessage = "Playing.";
            _errorMessage = null;
            PublishState();
        }
        catch (Exception exception)
        {
            ReportFailure("Playback", "The selected clip could not be played.", exception);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task PausePlaybackAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Playing);
            await _playback.PauseAsync(cancellationToken).ConfigureAwait(false);
            RequireActiveSession().PausePlayback();
            _mode = CoordinatorMode.Paused;
            _statusMessage = "Playback paused.";
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ResumePlaybackAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Paused);
            await _playback.ResumeAsync(cancellationToken).ConfigureAwait(false);
            RequireActiveSession().ResumePlayback();
            _mode = CoordinatorMode.Playing;
            _statusMessage = "Playing.";
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mode is not CoordinatorMode.Playing and not CoordinatorMode.Paused)
            {
                throw new DomainException("Seeking is only available during playback.");
            }

            var duration = RequireActiveSession().SelectedClip.Duration;
            var clamped = position > duration ? duration : position;
            await _playback.SeekAsync(clamped, cancellationToken).ConfigureAwait(false);
            _playbackPosition = clamped;
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopPlaybackAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mode is not CoordinatorMode.Playing and not CoordinatorMode.Paused)
            {
                throw new DomainException("Playback is not active.");
            }

            await StopPlaybackCoreAsync(cancellationToken).ConfigureAwait(false);
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> NewClipAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            var session = RequireActiveSession();
            var result = session.NewClip(Guid.NewGuid(), _clock.UtcNow);
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            _statusMessage = result.Created ? $"{result.Clip.DisplayName} is ready." : "Name this empty clip or start recording.";
            _playbackPosition = TimeSpan.Zero;
            PublishState();
            return result.Created;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task RenameSelectedClipAsync(string? displayName, CancellationToken cancellationToken = default) =>
        RenameClipAsync(RequireSession().SelectedClipId, displayName, cancellationToken);

    public async Task RenameClipAsync(Guid clipId, string? displayName, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            var session = RequireActiveSession();
            session.RenameClip(clipId, displayName);
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            _statusMessage = "Clip name saved.";
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SelectClipAsync(Guid clipId, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            RequireActiveSession().SelectClip(clipId);
            await _sessions.SaveAsync(RequireSession(), cancellationToken).ConfigureAwait(false);
            _playbackPosition = TimeSpan.Zero;
            _recordingPosition = TimeSpan.Zero;
            _livePeaks = [];
            _statusMessage = "Clip selected.";
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DeleteClipAsync(Guid clipId, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            var session = RequireActiveSession();
            var clip = session.ActiveClips.SingleOrDefault(item => item.Id == clipId)
                ?? throw new DomainException("The requested clip is unavailable.");
            TrashReceipt? receipt = null;
            if (clip.Segments.Count > 0)
            {
                receipt = await _workingStorage.MoveSegmentsToTrashAsync(session.Id, clip.Id, clip.Segments, cancellationToken).ConfigureAwait(false);
            }

            DeleteClipResult? deleteResult = null;
            try
            {
                deleteResult = session.DeleteClip(clip.Id, _clock.UtcNow, Guid.NewGuid());
                await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
                _pendingUndo = new(
                    PendingUndoKind.DeleteClip,
                    clip.Id,
                    clip.Segments.ToArray(),
                    receipt,
                    deleteResult.ReplacementCreated ? deleteResult.SelectedClip.Id : null);
            }
            catch
            {
                if (deleteResult is not null)
                {
                    session.UndoDeleteClip(
                        clip.Id,
                        deleteResult.ReplacementCreated ? deleteResult.SelectedClip.Id : null);
                }
                if (receipt is not null)
                {
                    await _workingStorage.RestoreTrashAsync(receipt, cancellationToken).ConfigureAwait(false);
                }
                throw;
            }

            _statusMessage = $"{clip.DisplayName} was deleted. Undo is available.";
            _playbackPosition = TimeSpan.Zero;
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task NewTakeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            var session = RequireActiveSession();
            var clip = session.SelectedClip;
            if (!clip.HasAudio)
            {
                throw new DomainException("This clip has no recording to reset.");
            }

            var oldSegments = clip.Segments.ToArray();
            var receipt = await _workingStorage.MoveSegmentsToTrashAsync(session.Id, clip.Id, oldSegments, cancellationToken).ConfigureAwait(false);
            try
            {
                session.ResetClip(clip.Id);
                await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                session.RestoreClipTake(clip.Id, oldSegments);
                await _workingStorage.RestoreTrashAsync(receipt, cancellationToken).ConfigureAwait(false);
                throw;
            }

            _pendingUndo = new(PendingUndoKind.ResetTake, clip.Id, oldSegments, receipt, null);
            _recordingPosition = TimeSpan.Zero;
            _playbackPosition = TimeSpan.Zero;
            _livePeaks = [];
            _statusMessage = "The clip is ready for a new take. Undo is available.";
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task UndoAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Ready);
            var pending = _pendingUndo ?? throw new DomainException("There is nothing to undo.");
            var session = RequireActiveSession();
            if (pending.Kind == PendingUndoKind.DeleteClip)
            {
                session.UndoDeleteClip(pending.ClipId, pending.ReplacementClipId);
            }
            else
            {
                session.RestoreClipTake(pending.ClipId, pending.Segments);
            }

            // Persist the restored references before moving files back. If power fails
            // between these steps, startup trash reconciliation completes the operation.
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            if (pending.Receipt is not null)
            {
                await _workingStorage.RestoreTrashAsync(pending.Receipt, cancellationToken).ConfigureAwait(false);
            }
            _pendingUndo = null;
            _statusMessage = "The previous change was undone.";
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(AudioDeviceFlow flow, CancellationToken cancellationToken = default) =>
        await _devices.GetDevicesAsync(flow, cancellationToken).ConfigureAwait(false);

    public async Task SelectInputDeviceAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default) =>
        await SelectDeviceAsync(selection, AudioDeviceFlow.Input, cancellationToken).ConfigureAwait(false);

    public async Task SelectOutputDeviceAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default) =>
        await SelectDeviceAsync(selection, AudioDeviceFlow.Output, cancellationToken).ConfigureAwait(false);

    public async Task<WaveformData> LoadSelectedWaveformAsync(int pixelWidth, TimeSpan? visibleStart = null, TimeSpan? visibleDuration = null, CancellationToken cancellationToken = default)
    {
        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        var session = RequireSession();
        var clip = session.SelectedClip;
        var paths = new List<string>();
        foreach (var segment in clip.Segments.Where(segment => segment.State == SegmentState.Finalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachePath = _workingStorage.GetAbsolutePeakCachePath(session.Id, segment);
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                continue;
            }

            if (!File.Exists(cachePath))
            {
                try
                {
                    var wavePath = _workingStorage.GetAbsoluteWavePath(session.Id, segment);
                    if (File.Exists(wavePath))
                    {
                        await _waveforms.BuildCacheAsync(
                            new WaveformCacheBuildRequest(wavePath, cachePath, segment.Format),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The audio remains playable/exportable. A later request may retry cache creation.
                }
            }

            if (File.Exists(cachePath))
            {
                paths.Add(cachePath);
            }
        }
        if (paths.Count == 0)
        {
            return new([], clip.Duration, 0);
        }

        return await _waveforms.LoadAsync(new(paths.ToArray(), pixelWidth, visibleStart, visibleDuration), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportResult> EndSessionAsync(string destinationId, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await PrepareActiveSessionForEndAsync(cancellationToken).ConfigureAwait(false);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            // A failed or interrupted Stop may leave older finalized audio beside a recoverable
            // continuation. Never use only the finalized subset for receipt lookup or export.
            session.EnsureCanExport();
            var previouslyCommitted = await _export.FindCommittedExportAsync(
                session.Id,
                GetFinalizedSegmentIds(session),
                _settings.Destinations,
                cancellationToken).ConfigureAwait(false);
            if (previouslyCommitted is not null)
            {
                session.CompleteRecoveredExport(_clock.UtcNow);
                _exportResult = previouslyCommitted;
                _exportProgress = new()
                {
                    State = ExportState.Completed,
                    FractionComplete = 1,
                    Message = "Recovered a completed destination export."
                };
                _mode = CoordinatorMode.Completed;
                _statusMessage = $"Recovered {previouslyCommitted.ExportedFilePaths.Count} already-saved clip(s).";
                _errorMessage = null;
                try
                {
                    await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
                    _completionManifestPersisted = true;
                }
                catch (Exception persistenceException)
                {
                    ReportNonFatalPersistenceFailure(
                        "Finalize recovered export",
                        "The audio files were already saved, but local completion status could not be recorded. Press Done to finish cleanup.",
                        persistenceException);
                    return previouslyCommitted;
                }
                PublishState();
                return previouslyCommitted;
            }
            var destination = _settings.Destinations.SingleOrDefault(item => item.IsEnabled && string.Equals(item.Id, destinationId, StringComparison.Ordinal))
                ?? throw new DomainException("The selected save destination is unavailable.");
            var validation = await _export.ValidateDestinationAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new IOException(validation.Message);
            }

            session.SelectDestination(destination.Id);
            var existingNames = await _export.GetExistingSessionFolderNamesAsync(destination, cancellationToken).ConfigureAwait(false);
            var names = OutputNamePlanner.Create(session, existingNames);
            var clips = session.ActiveClips.Where(clip => clip.HasAudio).OrderBy(clip => clip.Ordinal)
                .Select(clip => new ClipExportSource
                {
                    ClipId = clip.Id,
                    OutputFileName = names.GetFileName(clip.Id),
                    Segments = BuildPlaybackSources(session, clip),
                    Duration = clip.Duration
                })
                .ToArray();
            var request = new ExportRequest
            {
                SessionId = session.Id,
                SessionDisplayName = session.DisplayName,
                Destination = destination,
                SessionFolderName = names.SessionFolderName,
                Clips = clips,
                Format = session.SettingsSnapshot.RecordingFormat,
                Mp3BitrateKbps = session.SettingsSnapshot.Mp3BitrateKbps
            };

            session.BeginExport();
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            _mode = CoordinatorMode.Exporting;
            _exportProgress = new() { State = ExportState.Validating, FractionComplete = 0, Message = "Preparing export…" };
            _statusMessage = "Saving session…";
            _errorMessage = null;
            PublishState();

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _exportCancellation = linkedCancellation;
            ExportResult result;
            try
            {
                var progress = new Progress<ExportProgress>(value =>
                {
                    lock (_stateGate)
                    {
                        if (_mode != CoordinatorMode.Exporting)
                        {
                            return;
                        }
                        _exportProgress = value;
                    }
                    PublishState();
                });
                result = await _export.ExportAsync(request, progress, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                session.ExportCanceled();
                _mode = CoordinatorMode.Ready;
                _exportProgress = new() { State = ExportState.Canceled, FractionComplete = 0, Message = "Export canceled." };
                _statusMessage = "Export canceled. The local recording is safe.";
                try
                {
                    await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception persistenceException)
                {
                    ReportNonFatalPersistenceFailure(
                        "Cancel export",
                        "Export was canceled, but its local recovery status could not be saved.",
                        persistenceException);
                }
                PublishState();
                throw;
            }
            catch (Exception exception)
            {
                // ExportAsync only returns after destination commit. Therefore this branch is strictly pre-commit.
                if (session.State == SessionState.Exporting)
                {
                    session.ExportFailed(exception.Message);
                }
                _mode = CoordinatorMode.Ready;
                _exportProgress = new() { State = ExportState.Failed, FractionComplete = 0, Message = exception.Message };
                try
                {
                    await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception persistenceException)
                {
                    ReportNonFatalPersistenceFailure(
                        "Export failure recovery",
                        "Export failed and its local recovery status could not be saved.",
                        persistenceException);
                }
                ReportFailure(
                    "End session",
                    CreateRetryableFailureMessage("The session could not be saved.", exception),
                    exception);
                throw;
            }
            finally
            {
                _exportCancellation = null;
            }

            // ExportAsync returning is the commit boundary. Nothing after this point may call ExportFailed,
            // invite a duplicate retry, or leave the coordinator in Exporting mode.
            session.Complete(_clock.UtcNow);
            _exportResult = result;
            _exportProgress = new() { State = ExportState.Completed, FractionComplete = 1, Message = "Session saved." };
            _mode = CoordinatorMode.Completed;
            _statusMessage = $"Saved {result.ExportedFilePaths.Count} clip(s) to {destination.Label}.";
            _errorMessage = null;
            _completionManifestPersisted = false;

            try
            {
                await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
                _completionManifestPersisted = true;
            }
            catch (Exception persistenceException)
            {
                // The destination transaction is already committed. Report only the local bookkeeping problem;
                // the successful ExportResult and Completed state remain authoritative for this process.
                ReportNonFatalPersistenceFailure(
                    "Finalize exported session",
                    "The audio files were saved, but local completion status could not be recorded. Press Done to finish cleanup.",
                    persistenceException);
                return result;
            }

            PublishState();
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Ends a walk-away session that contains no recorded audio. This path deliberately
    /// bypasses all destination and export work so it cannot create an output folder.
    /// </summary>
    public async Task EndEmptySessionAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await PrepareActiveSessionForEndAsync(cancellationToken).ConfigureAwait(false);
            await CommitPendingUndoAsync(cancellationToken).ConfigureAwait(false);
            if (session.HasUnresolvedSegments)
            {
                throw new DomainException("A recording segment has not been safely finalized. Recover or reset it before ending the session.");
            }
            if (session.ActiveClips.Any(static clip => clip.HasAudio))
            {
                throw new DomainException("This session contains recorded audio and must be saved to an approved destination.");
            }

            // Both persistence services may own files beneath the local session root.
            // Neither operation touches an export destination.
            await _workingStorage.DeleteCompletedSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            await _sessions.DeleteAsync(session.Id, cancellationToken).ConfigureAwait(false);
            _session = null;
            _mode = CoordinatorMode.Welcome;
            _statusMessage = "Ready.";
            ClearTransientState();
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void CancelExport() => _exportCancellation?.Cancel();

    public async Task ResetAfterCompletedSessionAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireMode(CoordinatorMode.Completed);
            var session = RequireSession();
            var retentionHours = _settings.SessionBehavior.SuccessfulRecoveryRetentionHours;
            if (retentionHours == 0 || !_completionManifestPersisted)
            {
                // If the completion manifest could not be saved, keeping an old Exporting manifest would
                // incorrectly offer a duplicate export after restart. Successful destination data is already safe.
                await _workingStorage.DeleteCompletedSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                await _sessions.DeleteAsync(session.Id, cancellationToken).ConfigureAwait(false);
            }
            _session = null;
            _mode = CoordinatorMode.Welcome;
            _statusMessage = "Ready.";
            ClearTransientState();
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _exportCancellation?.Cancel();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_mode == CoordinatorMode.Recording)
            {
                try
                {
                    await StopRecordingCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        await _capture.AbortAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Disposal continues; StopRecordingCore has already retained recovery metadata.
                    }
                }
            }
            else if (_mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
            {
                try
                {
                    await StopPlaybackCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Playback has no unsaved audio and must not block shutdown.
                }
            }
            _disposed = true;
        }
        finally
        {
            _operationGate.Release();
        }

        _capture.ProgressChanged -= OnCaptureProgressChanged;
        _capture.Faulted -= OnCaptureFaulted;
        _playback.ProgressChanged -= OnPlaybackProgressChanged;
        _playback.StateChanged -= OnPlaybackStateChanged;
        _configuration.SettingsChanged -= OnSettingsChanged;
        _exportCancellation?.Cancel();
        await _capture.DisposeAsync().ConfigureAwait(false);
        await _playback.DisposeAsync().ConfigureAwait(false);
        await _devices.DisposeAsync().ConfigureAwait(false);
        _exportCancellation?.Dispose();
        _operationGate.Dispose();
    }

    private async Task StopRecordingCoreAsync(CancellationToken cancellationToken)
    {
        var session = RequireActiveSession();
        var allocation = _activeAllocation ?? throw new DomainException("The active recording allocation is missing.");
        var clipId = _activeClipId ?? throw new DomainException("The active recording clip is missing.");
        AudioCaptureResult result;
        try
        {
            result = await _capture.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            session.MarkActiveSegmentRecoverable(clipId, exception.Message);
            await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            _mode = CoordinatorMode.Ready;
            ClearActiveCapture();
            throw;
        }

        session.CompleteSegment(clipId, result.SegmentId, result.Duration, result.ByteCount, result.ChecksumSha256, _clock.UtcNow);
        _mode = CoordinatorMode.Ready;
        _statusMessage = "Recording paused. You can play it, resume recording, or start a new clip.";
        _errorMessage = null;
        ClearActiveCapture();
        await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);

        if (allocation.AbsolutePeakCachePath is not null)
        {
            try
            {
                await _waveforms.BuildCacheAsync(new(result.FinalWavePath, allocation.AbsolutePeakCachePath, session.SettingsSnapshot.RecordingFormat), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A missing visual cache must never downgrade a safely finalized recording.
                ReportFailure("Waveform", "The recording is safe, but its waveform could not be prepared.", exception);
            }
        }

    }

    /// <summary>
    /// Brings a session to a safe, idle state before either End Session path performs
    /// export or cleanup. An End Session click while recording always waits for the
    /// active WAV segment to finish finalizing before it considers the session complete.
    /// </summary>
    private async Task<RecordingSession> PrepareActiveSessionForEndAsync(CancellationToken cancellationToken)
    {
        var session = RequireActiveSession();
        if (_mode == CoordinatorMode.Recording)
        {
            try
            {
                await StopRecordingCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportFailure(
                    "Finalize recording before end session",
                    CreateRetryableFailureMessage("The recording could not be finalized.", exception),
                    exception);
                throw;
            }
        }
        else if (_mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
        {
            await StopPlaybackCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            RequireMode(CoordinatorMode.Ready);
        }

        return session;
    }

    private void ClearActiveCapture()
    {
        _activeAllocation = null;
        _activeClipId = null;
        _inputRms = 0;
        _inputPeak = 0;
    }

    private async Task StopPlaybackCoreAsync(CancellationToken cancellationToken)
    {
        await _playback.StopAsync(cancellationToken).ConfigureAwait(false);
        var session = RequireActiveSession();
        if (session.SelectedClip.State is ClipState.Playing or ClipState.Paused)
        {
            session.StopPlayback();
        }
        _mode = CoordinatorMode.Ready;
        _statusMessage = "Playback paused. You can resume recording or start a new clip.";
    }

    private async Task SelectDeviceAsync(AudioDeviceSelection selection, AudioDeviceFlow expectedFlow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Flow != expectedFlow)
        {
            throw new ArgumentException($"Expected a {expectedFlow} device.", nameof(selection));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mode is not CoordinatorMode.Welcome and not CoordinatorMode.Ready)
            {
                throw new DomainException("Devices can only be changed while audio is stopped.");
            }

            if (!_settings.Audio.AllowPatronDeviceSelection)
            {
                throw new DomainException("Audio device selection is locked by an administrator.");
            }

            var resolved = await _devices.ResolveAsync(selection, cancellationToken).ConfigureAwait(false);
            if (!selection.UseDefault && (resolved is null || !resolved.IsAvailable))
            {
                throw new DomainException("The selected audio device is unavailable.");
            }

            _settings = expectedFlow == AudioDeviceFlow.Input
                ? _settings with { Audio = _settings.Audio with { InputDevice = selection } }
                : _settings with { Audio = _settings.Audio with { OutputDevice = selection } };
            await _configuration.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
            if (_session?.State == SessionState.Active)
            {
                if (expectedFlow == AudioDeviceFlow.Input)
                {
                    _session.SetInputDevice(selection);
                }
                else
                {
                    _session.SetOutputDevice(selection);
                }
                await _sessions.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
            }

            _statusMessage = $"{(expectedFlow == AudioDeviceFlow.Input ? "Input" : "Output")} device saved.";
            _errorMessage = null;
            PublishState();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private IReadOnlyList<PlaybackSegmentSource> BuildPlaybackSources(RecordingSession session, RecordingClip clip) =>
        clip.Segments.Where(segment => segment.State == SegmentState.Finalized).OrderBy(segment => segment.Order)
            .Select(segment => new PlaybackSegmentSource
            {
                SegmentId = segment.Id,
                WavePath = _workingStorage.GetAbsoluteWavePath(session.Id, segment),
                Duration = segment.Duration
            }).ToArray();

    private static Guid[] GetFinalizedSegmentIds(RecordingSession session) =>
        session.ActiveClips
            .SelectMany(static clip => clip.Segments)
            .Where(static segment => segment.State == SegmentState.Finalized)
            .Select(static segment => segment.Id)
            .OrderBy(static id => id)
            .ToArray();

    private async Task CommitPendingUndoAsync(CancellationToken cancellationToken)
    {
        if (_pendingUndo?.Receipt is not null)
        {
            await _workingStorage.DeleteTrashAsync(_pendingUndo.Receipt, cancellationToken).ConfigureAwait(false);
        }
        _pendingUndo = null;
    }

    private async Task<IReadOnlyList<Exception>> CleanupExpiredCompletedSessionsAsync(CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        IReadOnlyList<RecordingSession> completed;
        try
        {
            completed = await _sessions.GetCompletedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
            return errors;
        }

        var cutoff = _clock.UtcNow - TimeSpan.FromHours(_settings.SessionBehavior.SuccessfulRecoveryRetentionHours);
        foreach (var session in completed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completedAt = session.CompletedAtUtc ?? session.StartedAtUtc;
            if (completedAt > cutoff)
            {
                continue;
            }

            try
            {
                await _workingStorage.DeleteCompletedSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
                await _sessions.DeleteAsync(session.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(exception);
            }
        }

        return errors;
    }

    private void OnCaptureProgressChanged(object? sender, AudioCaptureProgressEventArgs eventArgs)
    {
        lock (_stateGate)
        {
            if (_mode != CoordinatorMode.Recording)
            {
                return;
            }
            _recordingPosition = eventArgs.Duration;
            _inputRms = eventArgs.RmsLevel;
            _inputPeak = eventArgs.PeakLevel;
            _livePeaks = eventArgs.NewPeaks;
        }
        PublishState();
    }

    private void OnCaptureFaulted(object? sender, AudioCaptureFaultedEventArgs eventArgs) =>
        _ = HandleCaptureFaultAsync(eventArgs);

    private async Task HandleCaptureFaultAsync(AudioCaptureFaultedEventArgs eventArgs)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_mode != CoordinatorMode.Recording || _session is null || _activeClipId is null)
            {
                return;
            }

            try
            {
                await _capture.AbortAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve and report the original device/capture fault.
            }
            _session.MarkActiveSegmentRecoverable(_activeClipId.Value, eventArgs.UserMessage);
            await _sessions.SaveAsync(_session).ConfigureAwait(false);
            _activeAllocation = null;
            _activeClipId = null;
            _mode = CoordinatorMode.Ready;
            _inputRms = 0;
            _inputPeak = 0;
            ReportFailure("Record", eventArgs.UserMessage, eventArgs.Exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void OnPlaybackProgressChanged(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        lock (_stateGate)
        {
            if (_mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
            {
                _playbackPosition = eventArgs.Position;
            }
        }
        PublishState();
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs eventArgs)
    {
        if (eventArgs.State is PlaybackTransportState.Stopped or PlaybackTransportState.Faulted)
        {
            _ = HandlePlaybackEndedAsync(eventArgs);
        }
    }

    private async Task HandlePlaybackEndedAsync(PlaybackStateChangedEventArgs eventArgs)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session?.State != SessionState.Active || _session.SelectedClip.Id != eventArgs.ClipId)
            {
                return;
            }
            if (_session.SelectedClip.State is ClipState.Playing or ClipState.Paused)
            {
                _session.StopPlayback();
            }
            _mode = CoordinatorMode.Ready;
            _statusMessage = eventArgs.State == PlaybackTransportState.Faulted ? "Playback stopped because of an audio error." : "Playback finished.";
            if (eventArgs.State == PlaybackTransportState.Faulted)
            {
                ReportFailure("Playback", eventArgs.Error ?? "Playback stopped because of an audio error.", null);
            }
            else
            {
                PublishState();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs eventArgs)
    {
        // The live UI/content may change, while the active session continues using its frozen audio snapshot.
        _settings = eventArgs.Settings;
        PublishState();
    }

    private SessionCoordinatorState CreateState()
    {
        RecordingClip? selectedClip = null;
        if (_session is not null)
        {
            selectedClip = _session.ActiveClips.FirstOrDefault(clip => clip.Id == _session.SelectedClipId);
        }

        return new()
        {
            Version = _version,
            Mode = _mode,
            Settings = _settings,
            Session = _session,
            SelectedClip = selectedClip,
            Capabilities = BuildCapabilities(selectedClip),
            StatusMessage = _statusMessage,
            ErrorMessage = _errorMessage,
            RecordingPosition = _recordingPosition,
            PlaybackPosition = _playbackPosition,
            InputRmsLevel = _inputRms,
            InputPeakLevel = _inputPeak,
            LiveWaveformPeaks = _livePeaks,
            ExportProgress = _exportProgress,
            ExportResult = _exportResult
        };
    }

    private CoordinatorCapabilities BuildCapabilities(RecordingClip? clip)
    {
        var ready = _mode == CoordinatorMode.Ready && _session?.State == SessionState.Active;
        var canEndFromMode = _mode is CoordinatorMode.Ready or CoordinatorMode.Recording or CoordinatorMode.Playing or CoordinatorMode.Paused;
        var hasUnresolvedSegments = _session?.HasUnresolvedSegments == true;
        return new()
        {
            CanStartSession = _mode == CoordinatorMode.Welcome,
            CanRecord = ready && clip is not null,
            CanStop = _mode is CoordinatorMode.Recording or CoordinatorMode.Playing or CoordinatorMode.Paused,
            CanPlay = ready && clip?.HasAudio == true,
            CanPause = _mode == CoordinatorMode.Playing,
            CanResume = _mode == CoordinatorMode.Paused,
            CanSeek = _mode is CoordinatorMode.Playing or CoordinatorMode.Paused,
            CanEditClips = ready,
            CanNewTake = ready && clip?.Segments.Count > 0,
            CanEndSession = _session?.State == SessionState.Active && canEndFromMode &&
                            (_mode == CoordinatorMode.Recording || !hasUnresolvedSegments),
            CanChangeDevices = _settings.Audio.AllowPatronDeviceSelection && _mode is CoordinatorMode.Welcome or CoordinatorMode.Ready,
            CanUndo = ready && _pendingUndo is not null,
            CanCancelExport = _mode == CoordinatorMode.Exporting
        };
    }

    private void PublishState()
    {
        SessionCoordinatorState state;
        lock (_stateGate)
        {
            _version++;
            state = CreateState();
        }
        StateChanged?.Invoke(this, new() { State = state });
    }

    private void ReportFailure(string operation, string userMessage, Exception? exception)
    {
        _errorMessage = userMessage;
        _statusMessage = userMessage;
        PublishState();
        OperationFailed?.Invoke(this, new()
        {
            Operation = operation,
            UserMessage = userMessage,
            Exception = exception
        });
    }

    private static string CreateRetryableFailureMessage(string summary, Exception exception)
    {
        var detail = exception.GetBaseException().Message?.Trim();
        return string.IsNullOrWhiteSpace(detail)
            ? $"{summary} The local recording is safe and you can retry."
            : $"{summary} {detail} The local recording is safe and you can retry.";
    }

    private void ReportNonFatalPersistenceFailure(string operation, string userMessage, Exception exception)
    {
        _errorMessage = userMessage;
        PublishState();
        OperationFailed?.Invoke(this, new()
        {
            Operation = operation,
            UserMessage = userMessage,
            Exception = exception
        });
    }

    private void ClearTransientState()
    {
        _errorMessage = null;
        _recordingPosition = TimeSpan.Zero;
        _playbackPosition = TimeSpan.Zero;
        _inputRms = 0;
        _inputPeak = 0;
        _livePeaks = [];
        _exportProgress = null;
        _exportResult = null;
        _activeAllocation = null;
        _activeClipId = null;
        _pendingUndo = null;
        _completionManifestPersisted = false;
    }

    private RecordingSession RequireSession() => _session ?? throw new DomainException("There is no current session.");

    private RecordingSession RequireActiveSession()
    {
        var session = RequireSession();
        if (session.State != SessionState.Active)
        {
            throw new DomainException($"The session must be active; it is {session.State}.");
        }
        return session;
    }

    private void RequireMode(CoordinatorMode expected)
    {
        ThrowIfDisposed();
        if (_mode != expected)
        {
            throw new DomainException($"This action requires {expected} mode; the application is {_mode}.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private enum PendingUndoKind
    {
        DeleteClip,
        ResetTake
    }

    private sealed record PendingUndo(
        PendingUndoKind Kind,
        Guid ClipId,
        IReadOnlyList<RecordingSegment> Segments,
        TrashReceipt? Receipt,
        Guid? ReplacementClipId);
}
