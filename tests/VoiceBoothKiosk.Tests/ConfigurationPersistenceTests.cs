using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Configuration;
using VoiceBoothKiosk.Infrastructure.Persistence;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Tests;

public sealed class ConfigurationPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoiceBoothKiosk.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Shared_and_infrastructure_validators_reject_stereo_recording_configuration()
    {
        var settings = JsonConfigurationService.CreateOperationalDefaults() with
        {
            Audio = new AudioSettings
            {
                RecordingFormat = new AudioFormat(48_000, 2, 16)
            }
        };

        var shared = VoiceBoothKiosk.Application.KioskSettingsValidator.Validate(settings);
        var infrastructure = InfrastructureSettingsValidator.Validate(settings);

        Assert.Contains(shared.Errors, error => error.Path == "audio.recordingFormat.channels");
        Assert.Contains(infrastructure.Errors, error => error.Path == "audio.recordingFormat.channels");
    }

    [Fact]
    public void Shared_and_infrastructure_validators_reject_an_overlong_application_subtitle()
    {
        var settings = JsonConfigurationService.CreateOperationalDefaults() with
        {
            Branding = JsonConfigurationService.CreateOperationalDefaults().Branding with
            {
                ApplicationSubtitle = new string('S', 161)
            }
        };

        var shared = VoiceBoothKiosk.Application.KioskSettingsValidator.Validate(settings);
        var infrastructure = InfrastructureSettingsValidator.Validate(settings);

        Assert.Contains(shared.Errors, error => error.Path == "branding.applicationSubtitle");
        Assert.Contains(infrastructure.Errors, error => error.Path == "branding.applicationSubtitle");
    }

    [Fact]
    public void Instruction_text_formatter_renders_literal_and_actual_newlines()
    {
        var rendered = InstructionTextFormatter.ForDisplay("First\\nSecond\r\nThird");

        Assert.Equal(string.Join(Environment.NewLine, "First", "Second", "Third"), rendered);
    }

    [Fact]
    public async Task Application_subtitle_round_trips_through_machine_configuration()
    {
        var paths = CreatePaths();
        var configuration = new JsonConfigurationService(paths, new AtomicJsonFile());
        var defaults = JsonConfigurationService.CreateOperationalDefaults();
        var settings = defaults with
        {
            Branding = defaults.Branding with
            {
                ApplicationSubtitle = "Community stories, recorded here"
            },
            SessionBehavior = defaults.SessionBehavior with { SkipDestinationSelection = true }
        };

        await configuration.SaveAsync(settings);
        var reloaded = await configuration.LoadAsync();

        Assert.Equal("Community stories, recorded here", reloaded.Branding.ApplicationSubtitle);
        Assert.True(reloaded.SessionBehavior.SkipDestinationSelection);
    }

    [Fact]
    public void Skipping_destination_selection_requires_a_default_destination()
    {
        var defaults = JsonConfigurationService.CreateOperationalDefaults();
        var settings = defaults with
        {
            DefaultDestinationId = null,
            SessionBehavior = defaults.SessionBehavior with { SkipDestinationSelection = true }
        };

        var shared = VoiceBoothKiosk.Application.KioskSettingsValidator.Validate(settings);
        var infrastructure = InfrastructureSettingsValidator.Validate(settings);

        Assert.Contains(shared.Errors, error => error.Path == "sessionBehavior.skipDestinationSelection");
        Assert.Contains(infrastructure.Errors, error => error.Path == "sessionBehavior.skipDestinationSelection");
    }

    [Fact]
    public async Task Schema_one_welcome_supporting_text_migrates_to_two_instruction_blocks()
    {
        var paths = CreatePaths();
        var json = new AtomicJsonFile();
        var configuration = new JsonConfigurationService(paths, json);
        var defaults = JsonConfigurationService.CreateOperationalDefaults();
        var legacy = defaults with
        {
            SchemaVersion = 1,
            Branding = defaults.Branding with { WelcomeSupportingText = "Legacy supporting text" },
            ContentBlocks = defaults.ContentBlocks
                .Where(block => !block.Id.StartsWith("welcome.supporting.", StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
        await json.WriteAsync(paths.MachineSettingsPath, legacy);

        var migrated = await configuration.LoadAsync();

        Assert.Equal(KioskSettings.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Null(migrated.Branding.WelcomeSupportingText);
        Assert.Equal("Legacy supporting text", migrated.ContentBlocks.Single(block => block.Id == "welcome.supporting.1").Text);
        Assert.False(string.IsNullOrWhiteSpace(migrated.ContentBlocks.Single(block => block.Id == "welcome.supporting.2").Text));
    }

    [Fact]
    public async Task Layered_kiosk_settings_persist_only_patron_device_choices()
    {
        var paths = CreatePaths();
        var json = new AtomicJsonFile();
        var machine = new JsonConfigurationService(paths, json);
        var exportRoot = Path.Combine(_root, "exports");
        Directory.CreateDirectory(exportRoot);
        var machineSettings = JsonConfigurationService.CreateOperationalDefaults() with
        {
            Branding = new BrandingSettings { ApplicationTitle = "Locked station title" },
            Audio = new AudioSettings { AllowPatronDeviceSelection = true, Mp3BitrateKbps = 128 },
            Destinations =
            [
                new DestinationPreset
                {
                    Id = "test",
                    Label = "Test",
                    RootPath = exportRoot,
                    MinimumFreeBytes = 0
                }
            ],
            DefaultDestinationId = "test"
        };
        await machine.SaveAsync(machineSettings);
        var layered = new LayeredKioskConfigurationService(machine, json);

        var effective = await layered.LoadAsync();
        effective = effective with
        {
            Branding = effective.Branding with { ApplicationTitle = "Attempted public change" },
            Audio = effective.Audio with
            {
                InputDevice = new AudioDeviceSelection
                {
                    Flow = AudioDeviceFlow.Input,
                    EndpointId = "microphone-id",
                    FriendlyNameFallback = "Booth mic",
                    UseDefault = false
                }
            }
        };
        await layered.SaveAsync(effective);

        var reloadedMachine = await machine.LoadAsync();
        var reloadedLayered = await layered.LoadAsync();
        Assert.Equal("Locked station title", reloadedMachine.Branding.ApplicationTitle);
        Assert.Equal("Locked station title", reloadedLayered.Branding.ApplicationTitle);
        Assert.Equal("microphone-id", reloadedLayered.Audio.InputDevice.EndpointId);
        Assert.False(reloadedLayered.Audio.InputDevice.UseDefault);
        Assert.True(File.Exists(paths.UserSettingsPath));
    }

    [Fact]
    public async Task Session_manifest_round_trips_an_active_session()
    {
        var paths = CreatePaths();
        var json = new AtomicJsonFile();
        var storage = new FileWorkingStorage(paths, json);
        var repository = new JsonSessionRepository(paths, json);
        var session = RecordingSession.Create(
            "Morgan-Weather",
            DateTimeOffset.Now,
            DateTimeOffset.UtcNow,
            new SessionSettingsSnapshot());

        await storage.InitializeSessionAsync(session);
        await repository.SaveAsync(session);
        var loaded = await repository.LoadActiveAsync();

        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal("Morgan-Weather", loaded.DisplayName);
        Assert.Single(loaded.ActiveClips);
        Assert.Equal(session.SelectedClipId, loaded.SelectedClipId);
    }

    [Fact]
    public async Task Recoverable_session_remains_current_across_repeated_restarts()
    {
        var paths = CreatePaths();
        var json = new AtomicJsonFile();
        var session = RecordingSession.Create(
            "Restart-Recovery",
            DateTimeOffset.Now,
            DateTimeOffset.UtcNow,
            new SessionSettingsSnapshot());

        var repository = new JsonSessionRepository(paths, json);
        await repository.SaveAsync(session);
        var interrupted = await repository.LoadActiveAsync();
        Assert.NotNull(interrupted);
        interrupted.MarkRecoverable("Simulated process interruption.");
        await repository.SaveAsync(interrupted);

        for (var restart = 0; restart < 3; restart++)
        {
            repository = new JsonSessionRepository(paths, json);
            var rediscovered = await repository.LoadActiveAsync();

            Assert.NotNull(rediscovered);
            Assert.Equal(session.Id, rediscovered.Id);
            Assert.Equal(SessionState.Recoverable, rediscovered.State);
            Assert.Contains((await repository.GetRecoverableAsync()), candidate => candidate.Id == session.Id);
        }

        var resumable = await repository.LoadActiveAsync();
        Assert.NotNull(resumable);
        resumable.ResumeRecovered();
        await repository.SaveAsync(resumable);
        Assert.Equal(SessionState.Active, (await repository.LoadActiveAsync())!.State);
    }

    [Fact]
    public async Task Working_storage_allocates_contained_unique_paths()
    {
        var paths = CreatePaths();
        var storage = new FileWorkingStorage(paths);
        var session = RecordingSession.Create("Storage-Test", DateTimeOffset.Now, DateTimeOffset.UtcNow, new SessionSettingsSnapshot());
        await storage.InitializeSessionAsync(session);
        var segmentId = Guid.NewGuid();

        var allocation = await storage.AllocateSegmentAsync(session.Id, session.SelectedClipId, segmentId);

        Assert.StartsWith(paths.GetSessionDirectory(session.Id), allocation.AbsoluteFinalWavePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".wav.partial", allocation.AbsolutePartialWavePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.IsPathRooted(allocation.RelativeFinalWavePath));
        Assert.DoesNotContain("..", allocation.RelativeFinalWavePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_restores_files_when_trash_move_committed_before_session_manifest_update()
    {
        var paths = CreatePaths();
        var json = new AtomicJsonFile();
        var storage = new FileWorkingStorage(paths, json);
        var repository = new JsonSessionRepository(paths, json);
        var session = RecordingSession.Create("Trash-Rollback", DateTimeOffset.Now, DateTimeOffset.UtcNow, new SessionSettingsSnapshot());
        await storage.InitializeSessionAsync(session);
        var segmentId = Guid.NewGuid();
        var allocation = await storage.AllocateSegmentAsync(session.Id, session.SelectedClipId, segmentId);
        await File.WriteAllBytesAsync(allocation.AbsoluteFinalWavePath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(allocation.AbsolutePeakCachePath!, [5, 6]);
        session.BeginSegment(
            session.SelectedClipId,
            segmentId,
            allocation.RelativeFinalWavePath,
            allocation.RelativePeakCachePath,
            DateTimeOffset.UtcNow);
        session.CompleteSegment(session.SelectedClipId, segmentId, TimeSpan.FromSeconds(1), 4, null, DateTimeOffset.UtcNow);
        await repository.SaveAsync(session);

        var receipt = await storage.MoveSegmentsToTrashAsync(
            session.Id,
            session.SelectedClipId,
            session.SelectedClip.Segments);
        Assert.False(File.Exists(allocation.AbsoluteFinalWavePath));
        Assert.False(File.Exists(allocation.AbsolutePeakCachePath));

        var restartedRepository = new JsonSessionRepository(paths, json);
        var recovered = await restartedRepository.LoadActiveAsync();

        Assert.NotNull(recovered);
        Assert.True(File.Exists(allocation.AbsoluteFinalWavePath));
        Assert.True(File.Exists(allocation.AbsolutePeakCachePath));
        Assert.False(Directory.Exists(Path.Combine(paths.GetTrashDirectory(session.Id), receipt.Id.ToString("D"))));
        Assert.True(recovered.SelectedClip.HasAudio);
    }

    [Fact]
    public async Task Recovery_does_not_restore_trash_after_new_take_manifest_commits()
    {
        var paths = CreatePaths();
        var json = new AtomicJsonFile();
        var storage = new FileWorkingStorage(paths, json);
        var repository = new JsonSessionRepository(paths, json);
        var session = RecordingSession.Create("Trash-Committed", DateTimeOffset.Now, DateTimeOffset.UtcNow, new SessionSettingsSnapshot());
        await storage.InitializeSessionAsync(session);
        var segmentId = Guid.NewGuid();
        var allocation = await storage.AllocateSegmentAsync(session.Id, session.SelectedClipId, segmentId);
        await File.WriteAllBytesAsync(allocation.AbsoluteFinalWavePath, [1, 2, 3, 4]);
        session.BeginSegment(
            session.SelectedClipId,
            segmentId,
            allocation.RelativeFinalWavePath,
            allocation.RelativePeakCachePath,
            DateTimeOffset.UtcNow);
        session.CompleteSegment(session.SelectedClipId, segmentId, TimeSpan.FromSeconds(1), 4, null, DateTimeOffset.UtcNow);
        await repository.SaveAsync(session);

        var oldSegments = session.SelectedClip.Segments.ToArray();
        var receipt = await storage.MoveSegmentsToTrashAsync(session.Id, session.SelectedClipId, oldSegments);
        session.ResetClip(session.SelectedClipId);
        await repository.SaveAsync(session);

        var restartedRepository = new JsonSessionRepository(paths, json);
        var recovered = await restartedRepository.LoadActiveAsync();

        Assert.NotNull(recovered);
        Assert.False(recovered.SelectedClip.HasAudio);
        Assert.False(File.Exists(allocation.AbsoluteFinalWavePath));
        Assert.True(Directory.Exists(Path.Combine(paths.GetTrashDirectory(session.Id), receipt.Id.ToString("D"))));
    }

    private ApplicationPaths CreatePaths() => new(
        Path.Combine(_root, "machine"),
        Path.Combine(_root, "local"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
