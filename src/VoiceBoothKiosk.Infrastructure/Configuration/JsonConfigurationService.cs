using System.Text.Json;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Persistence;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Configuration;

public sealed class JsonConfigurationService : IConfigurationService
{
    private readonly AtomicJsonFile _json;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonConfigurationService(ApplicationPaths paths, AtomicJsonFile? json = null)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _json = json ?? new AtomicJsonFile();
    }

    public ApplicationPaths Paths { get; }
    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public async Task<KioskSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await _json.ReadAsync<KioskSettings>(Paths.MachineSettingsPath, cancellationToken).ConfigureAwait(false)
                ?? CreateOperationalDefaults();
            settings = Migrate(settings);
            var result = Validate(settings);
            if (!result.IsValid)
            {
                throw new InvalidDataException(FormatErrors("Configuration is invalid", result.Errors));
            }
            return Clone(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(KioskSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var saveCopy = Normalize(Clone(settings));
        var result = Validate(saveCopy);
        if (!result.IsValid)
        {
            throw new SettingsValidationException(result.Errors);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _json.WriteAsync(Paths.MachineSettingsPath, saveCopy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { Settings = Clone(saveCopy) });
    }

    public SettingsValidationResult Validate(KioskSettings settings)
    {
        var shared = VoiceBoothKiosk.Application.KioskSettingsValidator.Validate(settings);
        var infrastructure = InfrastructureSettingsValidator.Validate(settings);
        var errors = shared.Errors.Concat(infrastructure.Errors)
            .DistinctBy(error => (error.Path, error.Message))
            .ToArray();
        return new(errors);
    }

    public KioskSettings RestoreDefaults() => CreateOperationalDefaults();
    public KioskSettings Snapshot(KioskSettings settings) => Clone(settings);

    public static KioskSettings CreateOperationalDefaults()
    {
        var publicDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        if (string.IsNullOrWhiteSpace(publicDocuments))
            publicDocuments = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        var destination = new DestinationPreset
        {
            Id = "local-exports",
            Label = "Local Voice Booth Exports",
            RootPath = Path.Combine(publicDocuments, "Voice Booth Exports"),
            IsEnabled = true,
            IsLocked = true,
            MinimumFreeBytes = 1_073_741_824
        };
        var defaults = KioskSettings.CreateDefault();
        return defaults with
        {
            Destinations = [destination],
            DefaultDestinationId = destination.Id
        };
    }

    internal static KioskSettings Migrate(KioskSettings settings)
    {
        if (settings.SchemaVersion == 0) settings = settings with { SchemaVersion = 1 };
        if (settings.SchemaVersion > KioskSettings.CurrentSchemaVersion)
            throw new InvalidDataException($"Configuration schema {settings.SchemaVersion} is newer than this application supports.");

        if (settings.SchemaVersion < 2)
        {
            var branding = settings.Branding ?? new BrandingSettings();
            var blocks = (settings.ContentBlocks ?? []).ToList();
            var firstSupportingText = string.IsNullOrWhiteSpace(branding.WelcomeSupportingText)
                ? "Name your session, then record and review as many clips as you need."
                : branding.WelcomeSupportingText;
            InsertContentBlockAfter(
                blocks,
                "welcome.instructions",
                new ContentBlock { Id = "welcome.supporting.1", Text = firstSupportingText, Style = "Supporting" });
            InsertContentBlockAfter(
                blocks,
                "welcome.supporting.1",
                new ContentBlock
                {
                    Id = "welcome.supporting.2",
                    Text = "You can pause, review, and create additional clips before saving.",
                    Style = "Supporting"
                });
            settings = settings with
            {
                SchemaVersion = 2,
                Branding = branding with { WelcomeSupportingText = null },
                ContentBlocks = blocks
            };
        }
        if (settings.SchemaVersion < 3)
        {
            // Prior versions had no execution-state policy. Preserve the historical
            // kiosk expectation that a dedicated public recorder remains visible and
            // available, while leaving administrators free to turn it off explicitly.
            settings = settings with
            {
                SchemaVersion = 3,
                Kiosk = (settings.Kiosk ?? new KioskBehaviorSettings()) with { KeepAwake = true }
            };
        }
        // ApplicationVersion is release metadata, not an operator preference. Always stamp the
        // running build so sessions created from an older configuration report the true version.
        return settings with { ApplicationVersion = VoiceBoothRelease.Version };
    }

    private static void InsertContentBlockAfter(List<ContentBlock> blocks, string precedingId, ContentBlock block)
    {
        if (blocks.Any(existing => string.Equals(existing.Id, block.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var precedingIndex = blocks.FindIndex(existing =>
            string.Equals(existing.Id, precedingId, StringComparison.OrdinalIgnoreCase));
        blocks.Insert(precedingIndex < 0 ? blocks.Count : precedingIndex + 1, block);
    }

    private static KioskSettings Normalize(KioskSettings settings)
    {
        var destinations = settings.Destinations.Select(destination => destination with
        {
            RootPath = string.IsNullOrWhiteSpace(destination.RootPath)
                ? destination.RootPath
                : DestinationInspector.NormalizeApprovedRoot(destination.RootPath)
        }).ToArray();
        var logoPath = string.IsNullOrWhiteSpace(settings.Branding.LogoPath)
            ? null
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.Branding.LogoPath));
        return settings with
        {
            SchemaVersion = KioskSettings.CurrentSchemaVersion,
            ApplicationVersion = VoiceBoothRelease.Version,
            Branding = settings.Branding with { LogoPath = logoPath, WelcomeSupportingText = null },
            Destinations = destinations
        };
    }

    private KioskSettings Clone(KioskSettings settings)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, _json.SerializerOptions);
        return JsonSerializer.Deserialize<KioskSettings>(bytes, _json.SerializerOptions)
            ?? throw new InvalidDataException("The configuration snapshot could not be created.");
    }

    private static string FormatErrors(string heading, IReadOnlyList<SettingsValidationError> errors) =>
        heading + ":" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(error => $"- {error.Path}: {error.Message}"));
}

public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(IReadOnlyList<SettingsValidationError> errors)
        : base("Configuration is invalid:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(error => $"- {error.Path}: {error.Message}"))) => Errors = errors;

    public IReadOnlyList<SettingsValidationError> Errors { get; }
}

/// <summary>
/// Kiosk-facing wrapper that exposes the same validated reads but cannot persist machine
/// policy. Deployment ACLs remain the security boundary; this also prevents accidental
/// writes from public-recorder code.
/// </summary>
public sealed class ReadOnlyConfigurationService : IConfigurationService
{
    private readonly JsonConfigurationService _inner;

    public ReadOnlyConfigurationService(JsonConfigurationService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.SettingsChanged += (_, args) => SettingsChanged?.Invoke(this, args);
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
    public Task<KioskSettings> LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);
    public SettingsValidationResult Validate(KioskSettings settings) => _inner.Validate(settings);
    public Task SaveAsync(KioskSettings settings, CancellationToken cancellationToken = default) =>
        Task.FromException(new UnauthorizedAccessException(
            "Machine configuration is read-only in the recorder process. Open Settings to use VoiceBoothKiosk.Config."));
}

/// <summary>
/// Public-recorder configuration facade. It reads editor-owned machine settings,
/// then overlays the kiosk account's endpoint choices from LocalAppData only when machine
/// policy permits patron device selection. SaveAsync deliberately persists no other field.
/// </summary>
public sealed class LayeredKioskConfigurationService : IConfigurationService
{
    private readonly JsonConfigurationService _machine;
    private readonly AtomicJsonFile _json;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UserDeviceSettings? _cachedUserSettings;

    public LayeredKioskConfigurationService(JsonConfigurationService machine, AtomicJsonFile? json = null)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _json = json ?? new AtomicJsonFile();
        _machine.SettingsChanged += MachineSettingsChanged;
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    public async Task<KioskSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var machine = await _machine.LoadAsync(cancellationToken).ConfigureAwait(false);
            var user = await _json.ReadAsync<UserDeviceSettings>(
                _machine.Paths.UserSettingsPath, cancellationToken).ConfigureAwait(false);
            user = MigrateUserSettings(user);
            _cachedUserSettings = user;
            return Overlay(machine, user);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(KioskSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        KioskSettings effective;
        try
        {
            var machine = await _machine.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!machine.Audio.AllowPatronDeviceSelection)
            {
                throw new UnauthorizedAccessException("Device selection is locked by the administrator.");
            }

            var candidate = machine with
            {
                Audio = machine.Audio with
                {
                    InputDevice = settings.Audio.InputDevice,
                    OutputDevice = settings.Audio.OutputDevice
                }
            };
            var validation = _machine.Validate(candidate);
            var deviceErrors = validation.Errors.Where(error => error.Path.StartsWith("audio.inputDevice", StringComparison.Ordinal)
                || error.Path.StartsWith("audio.outputDevice", StringComparison.Ordinal)).ToArray();
            if (deviceErrors.Length > 0)
            {
                throw new SettingsValidationException(deviceErrors);
            }

            var user = new UserDeviceSettings
            {
                SchemaVersion = UserDeviceSettings.CurrentSchemaVersion,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                InputDevice = CopyDevice(settings.Audio.InputDevice, AudioDeviceFlow.Input),
                OutputDevice = CopyDevice(settings.Audio.OutputDevice, AudioDeviceFlow.Output)
            };
            await _json.WriteAsync(_machine.Paths.UserSettingsPath, user, cancellationToken).ConfigureAwait(false);
            _cachedUserSettings = user;
            effective = Overlay(machine, user);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { Settings = effective });
    }

    public SettingsValidationResult Validate(KioskSettings settings) => _machine.Validate(settings);

    private void MachineSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        var effective = Overlay(args.Settings, _cachedUserSettings);
        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { Settings = effective });
    }

    private static KioskSettings Overlay(KioskSettings machine, UserDeviceSettings? user)
    {
        if (!machine.Audio.AllowPatronDeviceSelection || user is null)
        {
            return machine;
        }

        return machine with
        {
            Audio = machine.Audio with
            {
                InputDevice = CopyDevice(user.InputDevice, AudioDeviceFlow.Input),
                OutputDevice = CopyDevice(user.OutputDevice, AudioDeviceFlow.Output)
            }
        };
    }

    private static UserDeviceSettings? MigrateUserSettings(UserDeviceSettings? settings)
    {
        if (settings is null) return null;
        if (settings.SchemaVersion == 0) settings = settings with { SchemaVersion = 1 };
        if (settings.SchemaVersion > UserDeviceSettings.CurrentSchemaVersion)
            throw new InvalidDataException($"User settings schema {settings.SchemaVersion} is newer than this application supports.");
        if (settings.InputDevice.Flow != AudioDeviceFlow.Input || settings.OutputDevice.Flow != AudioDeviceFlow.Output)
            throw new InvalidDataException("Saved user audio endpoints have invalid device-flow values.");
        return settings;
    }

    private static AudioDeviceSelection CopyDevice(AudioDeviceSelection source, AudioDeviceFlow flow) => new()
    {
        EndpointId = source.EndpointId,
        FriendlyNameFallback = source.FriendlyNameFallback,
        Flow = flow,
        UseDefault = source.UseDefault
    };
}

public sealed record UserDeviceSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public AudioDeviceSelection InputDevice { get; init; } = AudioDeviceSelection.WindowsDefault(AudioDeviceFlow.Input);
    public AudioDeviceSelection OutputDevice { get; init; } = AudioDeviceSelection.WindowsDefault(AudioDeviceFlow.Output);
}
