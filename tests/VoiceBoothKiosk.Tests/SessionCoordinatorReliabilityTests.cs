using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Tests;

public sealed class SessionCoordinatorReliabilityTests
{
    [Fact]
    public async Task Reload_configuration_on_welcome_applies_updated_settings_and_destinations()
    {
        var repositorySession = RecordingSession.Create(
            "Unused",
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 7, 16, 15, 0, 0, TimeSpan.Zero),
            new SessionSettingsSnapshot());
        repositorySession.Quarantine();
        var repository = new FaultInjectingSessionRepository(repositorySession);
        var initial = CreateSettings("before", "Before destination", "Voice Booth Before", 128);
        var configuration = new FakeConfigurationService(initial);
        await using var coordinator = CreateCoordinator(
            repositorySession,
            repository,
            new SuccessfulExportService(),
            configuration: configuration);

        await coordinator.InitializeAsync();
        Assert.Equal(CoordinatorMode.Welcome, coordinator.State.Mode);

        var updated = CreateSettings("after", "After destination", "Voice Booth After", 192);
        configuration.ReplaceSettings(updated);
        await coordinator.ReloadConfigurationAsync();

        Assert.Equal(CoordinatorMode.Welcome, coordinator.State.Mode);
        Assert.Null(coordinator.State.Session);
        Assert.Same(updated, coordinator.State.Settings);
        Assert.Equal("after", coordinator.State.Settings.DefaultDestinationId);
        Assert.Collection(
            coordinator.State.Settings.Destinations,
            destination =>
            {
                Assert.Equal("after", destination.Id);
                Assert.Equal("After destination", destination.Label);
            });
        Assert.Equal("Settings reloaded.", coordinator.State.StatusMessage);
        Assert.Equal(2, configuration.LoadCount);
    }

    [Fact]
    public async Task Reload_configuration_while_ready_preserves_session_and_its_settings_snapshot()
    {
        var repositorySession = RecordingSession.Create(
            "Unused",
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 7, 16, 15, 0, 0, TimeSpan.Zero),
            new SessionSettingsSnapshot());
        repositorySession.Quarantine();
        var repository = new FaultInjectingSessionRepository(repositorySession);
        var initial = CreateSettings("before", "Before destination", "Voice Booth Before", 128);
        var configuration = new FakeConfigurationService(initial);
        await using var coordinator = CreateCoordinator(
            repositorySession,
            repository,
            new SuccessfulExportService(),
            configuration: configuration);

        await coordinator.InitializeAsync();
        await coordinator.StartSessionAsync("Settings-Snapshot");
        var activeSession = coordinator.State.Session!;
        var originalSnapshot = activeSession.SettingsSnapshot;

        var updated = CreateSettings("after", "After destination", "Voice Booth After", 192);
        configuration.ReplaceSettings(updated);
        await coordinator.ReloadConfigurationAsync();

        Assert.Equal(CoordinatorMode.Ready, coordinator.State.Mode);
        Assert.Same(activeSession, coordinator.State.Session);
        Assert.Equal(originalSnapshot, coordinator.State.Session!.SettingsSnapshot);
        Assert.Equal(128, coordinator.State.Session.SettingsSnapshot.Mp3BitrateKbps);
        Assert.Same(updated, coordinator.State.Settings);
        Assert.Equal(192, coordinator.State.Settings.Audio.Mp3BitrateKbps);
        Assert.Equal("after", coordinator.State.Settings.DefaultDestinationId);
        Assert.Equal("Settings reloaded.", coordinator.State.StatusMessage);
    }

    [Fact]
    public async Task Reload_configuration_while_recording_is_refused_without_disturbing_state()
    {
        var repositorySession = RecordingSession.Create(
            "Unused",
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 7, 16, 15, 0, 0, TimeSpan.Zero),
            new SessionSettingsSnapshot());
        repositorySession.Quarantine();
        var repository = new FaultInjectingSessionRepository(repositorySession);
        var initial = CreateSettings("before", "Before destination", "Voice Booth Before", 128);
        var configuration = new FakeConfigurationService(initial);
        var capture = new FailingStopCaptureService();
        await using var coordinator = CreateCoordinator(
            repositorySession,
            repository,
            new SuccessfulExportService(),
            capture: capture,
            configuration: configuration);

        await coordinator.InitializeAsync();
        await coordinator.StartSessionAsync("Recording-State");
        await coordinator.StartRecordingAsync();
        var beforeReload = coordinator.State;
        configuration.ReplaceSettings(CreateSettings("after", "After destination", "Voice Booth After", 192));

        var error = await Assert.ThrowsAsync<DomainException>(() => coordinator.ReloadConfigurationAsync());

        Assert.Contains("stopped", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CoordinatorMode.Recording, coordinator.State.Mode);
        Assert.Same(beforeReload.Session, coordinator.State.Session);
        Assert.Equal(beforeReload.Session!.SettingsSnapshot, coordinator.State.Session!.SettingsSnapshot);
        Assert.Same(initial, coordinator.State.Settings);
        Assert.Equal(beforeReload.Version, coordinator.State.Version);
        Assert.True(capture.IsRecording);
    }

    [Fact]
    public async Task Reload_configuration_while_playing_is_refused_without_disturbing_state()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var initial = CreateSettings("approved", "Approved destination", "Voice Booth Before", 128);
        var configuration = new FakeConfigurationService(initial);
        var playback = new ControllablePlaybackService();
        await using var coordinator = CreateCoordinator(
            session,
            repository,
            new SuccessfulExportService(),
            configuration: configuration,
            playback: playback);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.PlaySelectedAsync();
        var beforeReload = coordinator.State;
        configuration.ReplaceSettings(CreateSettings("after", "After destination", "Voice Booth After", 192));

        var error = await Assert.ThrowsAsync<DomainException>(() => coordinator.ReloadConfigurationAsync());

        Assert.Contains("stopped", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CoordinatorMode.Playing, coordinator.State.Mode);
        Assert.Equal(PlaybackTransportState.Playing, playback.State);
        Assert.Same(beforeReload.Session, coordinator.State.Session);
        Assert.Equal(beforeReload.Session!.SettingsSnapshot, coordinator.State.Session!.SettingsSnapshot);
        Assert.Same(initial, coordinator.State.Settings);
        Assert.Equal(beforeReload.Version, coordinator.State.Version);
    }

    [Fact]
    public async Task Reload_configuration_waiting_on_an_export_is_refused_after_commit_without_loading_settings()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var initial = CreateSettings("approved", "Approved destination", "Voice Booth Before", 128);
        var configuration = new FakeConfigurationService(initial);
        var export = new BlockingExportService();
        await using var coordinator = CreateCoordinator(
            session,
            repository,
            export,
            configuration: configuration);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        var exportTask = coordinator.EndSessionAsync("approved");
        await export.ExportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CoordinatorMode.Exporting, coordinator.State.Mode);

        configuration.ReplaceSettings(CreateSettings("after", "After destination", "Voice Booth After", 192));
        var reloadTask = coordinator.ReloadConfigurationAsync();
        await Task.Delay(50);

        Assert.False(reloadTask.IsCompleted);
        Assert.Equal(1, configuration.LoadCount);
        Assert.Same(initial, coordinator.State.Settings);
        Assert.Equal(CoordinatorMode.Exporting, coordinator.State.Mode);

        export.AllowCommit();
        await exportTask;
        var error = await Assert.ThrowsAsync<DomainException>(() => reloadTask);

        Assert.Contains("stopped", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CoordinatorMode.Completed, coordinator.State.Mode);
        Assert.Same(initial, coordinator.State.Settings);
        Assert.Equal(1, configuration.LoadCount);
    }

    [Fact]
    public async Task Initialize_falls_back_to_recoverable_lookup_when_active_lookup_excludes_it()
    {
        var session = CreateRecordedSession();
        session.MarkRecoverable("Already recovered by an earlier process start.");
        var repository = new FaultInjectingSessionRepository(session)
        {
            ExcludeRecoverableFromActiveLookup = true
        };

        await using var coordinator = CreateCoordinator(session, repository, new SuccessfulExportService());
        await coordinator.InitializeAsync();

        Assert.Equal(CoordinatorMode.RecoveryAvailable, coordinator.State.Mode);
        Assert.Equal(session.Id, coordinator.State.Session?.Id);
        await coordinator.ResumeRecoveredSessionAsync();
        Assert.Equal(CoordinatorMode.Ready, coordinator.State.Mode);
        Assert.Equal(SessionState.Active, coordinator.State.Session?.State);
    }

    [Fact]
    public async Task End_empty_session_returns_to_welcome_without_exporting_or_creating_destination_work()
    {
        var repositorySession = RecordingSession.Create(
            "Unused",
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 7, 16, 15, 0, 0, TimeSpan.Zero),
            new SessionSettingsSnapshot());
        repositorySession.Quarantine();
        var repository = new FaultInjectingSessionRepository(repositorySession);
        var storage = new FakeWorkingStorage();
        var export = new SuccessfulExportService();
        await using var coordinator = CreateCoordinator(repositorySession, repository, export, storage: storage);

        await coordinator.InitializeAsync();
        await coordinator.StartSessionAsync("Walkaway-Test");
        var emptySessionId = Assert.IsType<Guid>(coordinator.State.Session?.Id);

        Assert.True(coordinator.State.Capabilities.CanEndSession);
        await coordinator.EndEmptySessionAsync();

        Assert.Equal(CoordinatorMode.Welcome, coordinator.State.Mode);
        Assert.Null(coordinator.State.Session);
        Assert.Contains(emptySessionId, repository.DeletedSessionIds);
        Assert.Contains(emptySessionId, storage.DeletedCompletedSessionIds);
        Assert.Equal(0, export.CommittedLookupCount);
        Assert.Equal(0, export.ExportCallCount);
    }

    [Fact]
    public async Task End_empty_session_refuses_to_discard_recorded_audio()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var storage = new FakeWorkingStorage();
        var export = new SuccessfulExportService();
        await using var coordinator = CreateCoordinator(session, repository, export, storage: storage);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => coordinator.EndEmptySessionAsync());

        Assert.Contains("must be saved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CoordinatorMode.Ready, coordinator.State.Mode);
        Assert.NotNull(coordinator.State.Session);
        Assert.Empty(repository.DeletedSessionIds);
        Assert.Empty(storage.DeletedCompletedSessionIds);
        Assert.Equal(0, export.CommittedLookupCount);
        Assert.Equal(0, export.ExportCallCount);
    }

    [Fact]
    public async Task Committed_export_remains_completed_when_final_manifest_save_fails()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session)
        {
            FailCompletedSaves = true
        };
        var export = new SuccessfulExportService();
        await using var coordinator = CreateCoordinator(session, repository, export);
        var failures = new List<ApplicationOperationFailedEventArgs>();
        coordinator.OperationFailed += (_, eventArgs) => failures.Add(eventArgs);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        var result = await coordinator.EndSessionAsync("approved");

        Assert.Single(result.ExportedFilePaths);
        Assert.Equal(1, export.ExportCallCount);
        Assert.Equal(CoordinatorMode.Completed, coordinator.State.Mode);
        Assert.Equal(SessionState.Completed, coordinator.State.Session?.State);
        Assert.Equal(ExportState.Completed, coordinator.State.ExportProgress?.State);
        Assert.Same(result, coordinator.State.ExportResult);
        Assert.Contains(failures, failure => failure.Operation == "Finalize exported session");
        Assert.Contains("audio files were saved", coordinator.State.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SessionState.Completed, repository.AttemptedSaveStates);

        await Assert.ThrowsAsync<DomainException>(() => coordinator.EndSessionAsync("approved"));
        Assert.Equal(1, export.ExportCallCount);
        Assert.Equal(CoordinatorMode.Completed, coordinator.State.Mode);
    }

    [Fact]
    public async Task Initialize_uses_durable_destination_receipt_instead_of_duplicate_export()
    {
        var session = CreateRecordedSession();
        session.BeginExport();
        var repository = new FaultInjectingSessionRepository(session);
        var committed = new ExportResult
        {
            SessionFolderPath = @"C:\exports\Reliability-Test_07162026",
            ExportedFilePaths = [@"C:\exports\Reliability-Test_07162026\Reliability-Test_01_07162026.mp3"]
        };
        var export = new SuccessfulExportService
        {
            CommittedResult = committed,
            CommittedSegmentIds = session.ActiveClips.SelectMany(clip => clip.Segments).Select(segment => segment.Id).ToArray()
        };
        await using var coordinator = CreateCoordinator(session, repository, export);

        await coordinator.InitializeAsync();

        Assert.Equal(CoordinatorMode.Completed, coordinator.State.Mode);
        Assert.Equal(SessionState.Completed, coordinator.State.Session?.State);
        Assert.Same(committed, coordinator.State.ExportResult);
        Assert.Equal(0, export.ExportCallCount);
        Assert.Contains(SessionState.Completed, repository.AttemptedSaveStates);
    }

    [Fact]
    public async Task Quarantine_active_session_hides_it_without_deleting_recovery_data()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var storage = new FakeWorkingStorage();
        await using var coordinator = CreateCoordinator(session, repository, new SuccessfulExportService(), storage: storage);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.QuarantineActiveSessionAsync();

        Assert.Equal(CoordinatorMode.Welcome, coordinator.State.Mode);
        Assert.Null(coordinator.State.Session);
        Assert.Contains(session.Id, repository.QuarantinedSessionIds);
        Assert.Contains(session.Id, storage.QuarantinedSessionIds);
        Assert.Empty(repository.DeletedSessionIds);
        Assert.Empty(storage.DeletedCompletedSessionIds);
    }

    [Fact]
    public async Task Completed_session_is_retained_then_cleaned_after_configured_interval()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var storage = new FakeWorkingStorage();
        await using (var coordinator = CreateCoordinator(session, repository, new SuccessfulExportService(), storage: storage))
        {
            await coordinator.InitializeAsync();
            await coordinator.ResumeRecoveredSessionAsync();
            await coordinator.EndSessionAsync("approved");
            await coordinator.ResetAfterCompletedSessionAsync();

            Assert.Equal(CoordinatorMode.Welcome, coordinator.State.Mode);
            Assert.Empty(repository.DeletedSessionIds);
            Assert.Empty(storage.DeletedCompletedSessionIds);
        }

        var completedAt = session.CompletedAtUtc!.Value;
        await using var restarted = CreateCoordinator(
            session,
            repository,
            new SuccessfulExportService(),
            storage: storage,
            clock: new FixedClock(completedAt.ToLocalTime().AddHours(25), completedAt.AddHours(25)));
        await restarted.InitializeAsync();

        Assert.Contains(session.Id, repository.DeletedSessionIds);
        Assert.Contains(session.Id, storage.DeletedCompletedSessionIds);
        Assert.Equal(CoordinatorMode.Welcome, restarted.State.Mode);
    }

    [Fact]
    public async Task Zero_completed_session_retention_deletes_local_copy_on_reset()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var storage = new FakeWorkingStorage();
        await using var coordinator = CreateCoordinator(
            session,
            repository,
            new SuccessfulExportService(),
            storage: storage,
            retentionHours: 0);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.EndSessionAsync("approved");
        await coordinator.ResetAfterCompletedSessionAsync();

        Assert.Contains(session.Id, repository.DeletedSessionIds);
        Assert.Contains(session.Id, storage.DeletedCompletedSessionIds);
    }

    [Fact]
    public async Task Failed_continued_stop_keeps_segment_recoverable_and_blocks_subset_export()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var storage = new FakeWorkingStorage();
        var export = new SuccessfulExportService();
        var capture = new FailingStopCaptureService();
        await using var coordinator = CreateCoordinator(
            session,
            repository,
            export,
            storage: storage,
            capture: capture);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.StartRecordingAsync();

        await Assert.ThrowsAsync<IOException>(() => coordinator.StopRecordingAsync());

        Assert.Equal(CoordinatorMode.Ready, coordinator.State.Mode);
        Assert.Equal(SessionState.Active, session.State);
        Assert.Single(session.SelectedClip.Segments, segment => segment.State == SegmentState.Finalized);
        Assert.Single(session.SelectedClip.Segments, segment => segment.State == SegmentState.Recoverable);
        Assert.True(session.HasUnresolvedSegments);
        Assert.False(coordinator.State.Capabilities.CanEndSession);

        var error = await Assert.ThrowsAsync<DomainException>(() => coordinator.EndSessionAsync("approved"));

        Assert.Contains("not been safely finalized", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, export.CommittedLookupCount);
        Assert.Equal(0, export.ExportCallCount);
        Assert.NotEqual(SessionState.Completed, session.State);
        Assert.Empty(storage.DeletedCompletedSessionIds);
    }

    [Fact]
    public async Task Pausing_recording_finalizes_a_segment_and_allows_the_same_clip_to_resume()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var capture = new SuccessfulCaptureService();
        await using var coordinator = CreateCoordinator(
            session,
            repository,
            new SuccessfulExportService(),
            capture: capture);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        var originalSegmentCount = session.SelectedClip.Segments.Count;

        await coordinator.StartRecordingAsync();
        Assert.Equal(CoordinatorMode.Recording, coordinator.State.Mode);
        Assert.True(coordinator.State.Capabilities.CanStop);

        await coordinator.StopRecordingAsync();

        Assert.Equal(CoordinatorMode.Ready, coordinator.State.Mode);
        Assert.Contains("paused", coordinator.State.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.State.Capabilities.CanRecord);
        Assert.True(coordinator.State.Capabilities.CanEditClips);
        Assert.Equal(originalSegmentCount + 1, session.SelectedClip.Segments.Count);

        await coordinator.StartRecordingAsync();
        await coordinator.StopRecordingAsync();

        Assert.Equal(2, capture.StartCount);
        Assert.Equal(originalSegmentCount + 2, session.SelectedClip.Segments.Count);
    }

    [Fact]
    public async Task End_session_finalizes_an_in_progress_recording_before_export()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var export = new SuccessfulExportService();
        var capture = new SuccessfulCaptureService();
        await using var coordinator = CreateCoordinator(session, repository, export, capture: capture);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.StartRecordingAsync();

        await coordinator.EndSessionAsync("approved");

        Assert.Equal(1, capture.StopCount);
        Assert.Equal(CoordinatorMode.Completed, coordinator.State.Mode);
        Assert.Equal(SessionState.Completed, session.State);
        var request = Assert.Single(export.Requests);
        var clip = Assert.Single(request.Clips);
        Assert.Equal(2, clip.Segments.Count);
    }

    [Fact]
    public async Task End_session_exports_paused_segments_for_a_single_clip()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var export = new SuccessfulExportService();
        var capture = new SuccessfulCaptureService();
        await using var coordinator = CreateCoordinator(session, repository, export, capture: capture);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.StartRecordingAsync();
        await coordinator.StopRecordingAsync();
        await coordinator.StartRecordingAsync();
        await coordinator.StopRecordingAsync();

        await coordinator.EndSessionAsync("approved");

        Assert.Equal(2, capture.StopCount);
        Assert.Equal(SessionState.Completed, session.State);
        var request = Assert.Single(export.Requests);
        var clip = Assert.Single(request.Clips);
        Assert.Equal(3, clip.Segments.Count);
        Assert.Equal(3, request.Clips.Sum(item => item.Segments.Count));
    }

    [Fact]
    public async Task End_session_exports_paused_segments_for_multiple_clips()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var export = new SuccessfulExportService();
        var capture = new SuccessfulCaptureService();
        await using var coordinator = CreateCoordinator(session, repository, export, capture: capture);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.NewClipAsync();
        await coordinator.StartRecordingAsync();
        await coordinator.StopRecordingAsync();

        await coordinator.EndSessionAsync("approved");

        Assert.Equal(SessionState.Completed, session.State);
        var request = Assert.Single(export.Requests);
        Assert.Equal(2, request.Clips.Count);
        Assert.All(request.Clips, clip => Assert.NotEmpty(clip.Segments));
    }

    [Fact]
    public async Task End_session_reports_the_export_failure_detail_after_a_paused_recording()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var export = new FailingExportService(new InvalidDataException("The finalized WAV segment is invalid."));
        var capture = new SuccessfulCaptureService();
        await using var coordinator = CreateCoordinator(session, repository, export, capture: capture);
        ApplicationOperationFailedEventArgs? failure = null;
        coordinator.OperationFailed += (_, args) => failure = args;

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.StartRecordingAsync();
        await coordinator.StopRecordingAsync();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.EndSessionAsync("approved"));

        Assert.Equal("The finalized WAV segment is invalid.", exception.Message);
        Assert.NotNull(failure);
        Assert.Equal("End session", failure!.Operation);
        Assert.Contains("The finalized WAV segment is invalid.", failure.UserMessage, StringComparison.Ordinal);
        Assert.Contains("local recording is safe", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CoordinatorMode.Ready, coordinator.State.Mode);
        Assert.Equal(SessionState.Active, session.State);
    }

    [Fact]
    public async Task Pausing_playback_returns_to_ready_for_recording_and_new_clips()
    {
        var session = CreateRecordedSession();
        var repository = new FaultInjectingSessionRepository(session);
        var playback = new ControllablePlaybackService();
        await using var coordinator = CreateCoordinator(
            session,
            repository,
            new SuccessfulExportService(),
            playback: playback);

        await coordinator.InitializeAsync();
        await coordinator.ResumeRecoveredSessionAsync();
        await coordinator.PlaySelectedAsync();
        playback.RaiseProgress(new PlaybackProgressEventArgs
        {
            ClipId = session.SelectedClipId,
            Position = TimeSpan.FromSeconds(4),
            Duration = session.SelectedClip.Duration
        });

        await coordinator.StopPlaybackAsync();

        Assert.Equal(CoordinatorMode.Ready, coordinator.State.Mode);
        Assert.Equal(TimeSpan.FromSeconds(4), coordinator.State.PlaybackPosition);
        Assert.Contains("paused", coordinator.State.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.State.Capabilities.CanRecord);
        Assert.True(coordinator.State.Capabilities.CanEditClips);
        Assert.Equal(PlaybackTransportState.Stopped, playback.State);

        Assert.True(await coordinator.NewClipAsync());
    }

    private static SessionCoordinator CreateCoordinator(
        RecordingSession session,
        ISessionRepository repository,
        IExportService export,
        FakeWorkingStorage? storage = null,
        IClock? clock = null,
        int retentionHours = 24,
        IAudioCaptureService? capture = null,
        IConfigurationService? configuration = null,
        IAudioPlaybackService? playback = null)
    {
        var destinationRoot = Path.Combine(Path.GetTempPath(), "VoiceBoothKiosk.Tests", "exports");
        var settings = KioskSettings.CreateDefault() with
        {
            Destinations =
            [
                new DestinationPreset
                {
                    Id = "approved",
                    Label = "Approved destination",
                    RootPath = destinationRoot,
                    MinimumFreeBytes = 0
                }
            ],
            DefaultDestinationId = "approved",
            SessionBehavior = new SessionBehaviorSettings { SuccessfulRecoveryRetentionHours = retentionHours }
        };

        return new SessionCoordinator(
            capture ?? new UnusedCaptureService(),
            playback ?? new UnusedPlaybackService(),
            new UnusedDeviceService(),
            new UnusedWaveformService(),
            export,
            repository,
            storage ?? new FakeWorkingStorage(),
            configuration ?? new FakeConfigurationService(settings),
            clock ?? new FixedClock(session.StartedAtLocal, session.StartedAtUtc.AddMinutes(5)));
    }

    private static KioskSettings CreateSettings(
        string destinationId,
        string destinationLabel,
        string applicationTitle,
        int bitrateKbps)
    {
        return KioskSettings.CreateDefault() with
        {
            Branding = new BrandingSettings { ApplicationTitle = applicationTitle },
            Audio = new AudioSettings { Mp3BitrateKbps = bitrateKbps },
            Destinations =
            [
                new DestinationPreset
                {
                    Id = destinationId,
                    Label = destinationLabel,
                    RootPath = Path.Combine(Path.GetTempPath(), "VoiceBoothKiosk.Tests", destinationId),
                    MinimumFreeBytes = 0
                }
            ],
            DefaultDestinationId = destinationId
        };
    }

    private static RecordingSession CreateRecordedSession()
    {
        var local = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.FromHours(-5));
        var utc = local.ToUniversalTime();
        var session = RecordingSession.Create("Reliability-Test", local, utc, new SessionSettingsSnapshot());
        var segmentId = Guid.NewGuid();
        session.BeginSegment(
            session.SelectedClipId,
            segmentId,
            $"segments/{session.SelectedClipId}/{segmentId}.wav",
            null,
            utc);
        session.CompleteSegment(session.SelectedClipId, segmentId, TimeSpan.FromSeconds(12), 128, "checksum", utc.AddSeconds(12));
        return session;
    }

    private sealed class FaultInjectingSessionRepository(RecordingSession session) : ISessionRepository
    {
        public bool ExcludeRecoverableFromActiveLookup { get; init; }
        public bool FailCompletedSaves { get; init; }
        public List<SessionState> AttemptedSaveStates { get; } = [];
        public List<Guid> DeletedSessionIds { get; } = [];
        public List<Guid> QuarantinedSessionIds { get; } = [];

        public Task SaveAsync(RecordingSession value, CancellationToken cancellationToken = default)
        {
            AttemptedSaveStates.Add(value.State);
            if (FailCompletedSaves && value.State == SessionState.Completed)
            {
                throw new IOException("Injected completed-manifest failure.");
            }
            return Task.CompletedTask;
        }

        public Task<RecordingSession?> LoadActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<RecordingSession?>(
                session.State is SessionState.Completed or SessionState.Quarantined ||
                ExcludeRecoverableFromActiveLookup && session.State == SessionState.Recoverable
                    ? null
                    : session);

        public Task<IReadOnlyList<RecordingSession>> GetRecoverableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecordingSession>>(session.State == SessionState.Recoverable ? [session] : []);

        public Task<IReadOnlyList<RecordingSession>> GetCompletedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecordingSession>>(session.State == SessionState.Completed && !DeletedSessionIds.Contains(session.Id) ? [session] : []);

        public Task QuarantineAsync(RecordingSession value, CancellationToken cancellationToken = default)
        {
            QuarantinedSessionIds.Add(value.Id);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            DeletedSessionIds.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class SuccessfulExportService : IExportService
    {
        public int ExportCallCount { get; private set; }
        public int CommittedLookupCount { get; private set; }
        public ExportResult? CommittedResult { get; init; }
        public IReadOnlyCollection<Guid> CommittedSegmentIds { get; init; } = [];
        public List<ExportRequest> Requests { get; } = [];

        public Task<DestinationValidationResult> ValidateDestinationAsync(DestinationPreset destination, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DestinationValidationResult { IsValid = true, Message = "Ready", AvailableBytes = long.MaxValue });

        public Task<IReadOnlyCollection<string>> GetExistingSessionFolderNamesAsync(DestinationPreset destination, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<ExportResult?> FindCommittedExportAsync(
            Guid sessionId,
            IReadOnlyCollection<Guid> finalizedSegmentIds,
            IReadOnlyList<DestinationPreset> destinations,
            CancellationToken cancellationToken = default)
        {
            CommittedLookupCount++;
            return Task.FromResult(
                CommittedResult is not null && finalizedSegmentIds.ToHashSet().SetEquals(CommittedSegmentIds)
                    ? CommittedResult
                    : null);
        }

        public Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            ExportCallCount++;
            Requests.Add(request);
            progress?.Report(new ExportProgress { State = ExportState.Committing, FractionComplete = 1, Message = "Committed" });
            return Task.FromResult(new ExportResult
            {
                SessionFolderPath = Path.Combine(request.Destination.RootPath, request.SessionFolderName),
                ExportedFilePaths = [Path.Combine(request.Destination.RootPath, request.SessionFolderName, request.Clips[0].OutputFileName)]
            });
        }
    }

    private sealed class FailingExportService(Exception exception) : IExportService
    {
        public Task<DestinationValidationResult> ValidateDestinationAsync(DestinationPreset destination, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DestinationValidationResult { IsValid = true, Message = "Ready", AvailableBytes = long.MaxValue });

        public Task<IReadOnlyCollection<string>> GetExistingSessionFolderNamesAsync(DestinationPreset destination, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>([]);

        public Task<ExportResult?> FindCommittedExportAsync(
            Guid sessionId,
            IReadOnlyCollection<Guid> finalizedSegmentIds,
            IReadOnlyList<DestinationPreset> destinations,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ExportResult?>(null);

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            IProgress<ExportProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ExportResult>(exception);
    }

    private sealed class BlockingExportService : IExportService
    {
        private readonly TaskCompletionSource _allowCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DestinationValidationResult> ValidateDestinationAsync(
            DestinationPreset destination,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DestinationValidationResult { IsValid = true, Message = "Ready", AvailableBytes = long.MaxValue });

        public Task<IReadOnlyCollection<string>> GetExistingSessionFolderNamesAsync(
            DestinationPreset destination,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>([]);

        public async Task<ExportResult> ExportAsync(
            ExportRequest request,
            IProgress<ExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ExportStarted.TrySetResult();
            await _allowCommit.Task.WaitAsync(cancellationToken);
            return new ExportResult
            {
                SessionFolderPath = Path.Combine(request.Destination.RootPath, request.SessionFolderName),
                ExportedFilePaths = request.Clips
                    .Select(clip => Path.Combine(request.Destination.RootPath, request.SessionFolderName, clip.OutputFileName))
                    .ToArray()
            };
        }

        public void AllowCommit() => _allowCommit.TrySetResult();
    }

    private sealed class FakeConfigurationService(KioskSettings settings) : IConfigurationService
    {
        private KioskSettings _settings = settings;

        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
        public int LoadCount { get; private set; }

        public Task<KioskSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(_settings);
        }
        public Task SaveAsync(KioskSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public SettingsValidationResult Validate(KioskSettings value) => KioskSettingsValidator.Validate(value);

        public void ReplaceSettings(KioskSettings value) => _settings = value;

        public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { Settings = _settings });
    }

    private sealed class FakeWorkingStorage : IWorkingStorage
    {
        public List<Guid> QuarantinedSessionIds { get; } = [];
        public List<Guid> DeletedCompletedSessionIds { get; } = [];

        public Task InitializeSessionAsync(RecordingSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<WorkingSegmentAllocation> AllocateSegmentAsync(Guid sessionId, Guid clipId, Guid segmentId, CancellationToken cancellationToken = default)
        {
            var relative = Path.Combine("segments", clipId.ToString("D"), $"{segmentId:D}.wav");
            var final = Path.Combine(Path.GetTempPath(), "VoiceBoothKiosk.Tests", sessionId.ToString("D"), relative);
            return Task.FromResult(new WorkingSegmentAllocation
            {
                SegmentId = segmentId,
                RelativeFinalWavePath = relative,
                RelativePeakCachePath = null,
                AbsolutePartialWavePath = final + ".partial",
                AbsoluteFinalWavePath = final,
                AbsolutePeakCachePath = null
            });
        }
        public string GetAbsoluteWavePath(Guid sessionId, RecordingSegment segment) => Path.Combine(Path.GetTempPath(), segment.Id + ".wav");
        public string? GetAbsolutePeakCachePath(Guid sessionId, RecordingSegment segment) => null;
        public Task<long> GetAvailableBytesAsync(CancellationToken cancellationToken = default) => Task.FromResult(long.MaxValue);
        public Task<TrashReceipt> MoveSegmentsToTrashAsync(Guid sessionId, Guid clipId, IReadOnlyList<RecordingSegment> segments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreTrashAsync(TrashReceipt receipt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteTrashAsync(TrashReceipt receipt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task QuarantineAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            QuarantinedSessionIds.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task DeleteCompletedSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            DeletedCompletedSessionIds.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedCaptureService : IAudioCaptureService
    {
        public bool IsRecording => false;
        public event EventHandler<AudioCaptureProgressEventArgs>? ProgressChanged;
        public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;
        public Task StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseProgress(AudioCaptureProgressEventArgs value) => ProgressChanged?.Invoke(this, value);
        public void RaiseFault(AudioCaptureFaultedEventArgs value) => Faulted?.Invoke(this, value);
    }

    private sealed class FailingStopCaptureService : IAudioCaptureService
    {
        public bool IsRecording { get; private set; }
        public event EventHandler<AudioCaptureProgressEventArgs>? ProgressChanged;
        public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

        public Task StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            throw new IOException("Injected failure while finalizing the continued recording.");
        }

        public Task AbortAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseProgress(AudioCaptureProgressEventArgs value) => ProgressChanged?.Invoke(this, value);
        public void RaiseFault(AudioCaptureFaultedEventArgs value) => Faulted?.Invoke(this, value);
    }

    private sealed class SuccessfulCaptureService : IAudioCaptureService
    {
        private AudioCaptureRequest? _request;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsRecording { get; private set; }
        public event EventHandler<AudioCaptureProgressEventArgs>? ProgressChanged;
        public event EventHandler<AudioCaptureFaultedEventArgs>? Faulted;

        public Task StartAsync(AudioCaptureRequest request, CancellationToken cancellationToken = default)
        {
            _request = request;
            StartCount++;
            IsRecording = true;
            return Task.CompletedTask;
        }

        public Task<AudioCaptureResult> StopAsync(CancellationToken cancellationToken = default)
        {
            var request = _request ?? throw new InvalidOperationException("Recording has not started.");
            IsRecording = false;
            _request = null;
            StopCount++;
            return Task.FromResult(new AudioCaptureResult
            {
                SegmentId = request.SegmentId,
                Duration = TimeSpan.FromSeconds(1),
                ByteCount = 96_000,
                FinalWavePath = request.FinalWavePath
            });
        }

        public Task AbortAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            _request = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseProgress(AudioCaptureProgressEventArgs value) => ProgressChanged?.Invoke(this, value);
        public void RaiseFault(AudioCaptureFaultedEventArgs value) => Faulted?.Invoke(this, value);
    }

    private sealed class UnusedPlaybackService : IAudioPlaybackService
    {
        public PlaybackTransportState State => PlaybackTransportState.Stopped;
        public TimeSpan Position => TimeSpan.Zero;
        public event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;
        public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;
        public Task PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PauseAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseProgress(PlaybackProgressEventArgs value) => ProgressChanged?.Invoke(this, value);
        public void RaiseState(PlaybackStateChangedEventArgs value) => StateChanged?.Invoke(this, value);
    }

    private sealed class ControllablePlaybackService : IAudioPlaybackService
    {
        public PlaybackTransportState State { get; private set; } = PlaybackTransportState.Stopped;
        public TimeSpan Position { get; private set; }
        public event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;
        public event EventHandler<PlaybackStateChangedEventArgs>? StateChanged;

        public Task PlayAsync(AudioPlaybackRequest request, CancellationToken cancellationToken = default)
        {
            State = PlaybackTransportState.Playing;
            Position = request.StartPosition;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default)
        {
            State = PlaybackTransportState.Paused;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            State = PlaybackTransportState.Playing;
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            Position = position;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = PlaybackTransportState.Stopped;
            Position = TimeSpan.Zero;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseProgress(PlaybackProgressEventArgs value) => ProgressChanged?.Invoke(this, value);
        public void RaiseState(PlaybackStateChangedEventArgs value) => StateChanged?.Invoke(this, value);
    }

    private sealed class UnusedDeviceService : IAudioDeviceService
    {
        public event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;
        public Task<IReadOnlyList<AudioDeviceInfo>> GetDevicesAsync(AudioDeviceFlow flow, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);
        public Task<AudioDeviceInfo?> ResolveAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default) => Task.FromResult<AudioDeviceInfo?>(null);
        public Task<AudioDeviceTestResult> MonitorInputAsync(AudioDeviceSelection selection, IProgress<float> levelProgress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AudioDeviceTestResult> TestOutputAsync(AudioDeviceSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void RaiseChanged() => DevicesChanged?.Invoke(this, new AudioDevicesChangedEventArgs());
    }

    private sealed class UnusedWaveformService : IWaveformService
    {
        public Task BuildCacheAsync(WaveformCacheBuildRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WaveformData> LoadAsync(WaveformLoadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset localNow, DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
        public DateTimeOffset LocalNow { get; } = localNow;
    }
}
