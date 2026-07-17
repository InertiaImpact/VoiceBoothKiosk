using System.Globalization;
using System.Text.RegularExpressions;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Logging;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Configuration;

public static partial class InfrastructureSettingsValidator
{
    private static readonly HashSet<int> SampleRates = [48_000];
    private static readonly HashSet<int> Bitrates = [96, 128, 160, 192, 256, 320];

    public static SettingsValidationResult Validate(KioskSettings? settings)
    {
        var errors = new List<SettingsValidationError>();
        if (settings is null)
        {
            return new([new("$", "Configuration is missing.")]);
        }

        if (settings.SchemaVersion != KioskSettings.CurrentSchemaVersion)
            Add(errors, "schemaVersion", $"Expected schema version {KioskSettings.CurrentSchemaVersion}.");

        if (settings.Branding is null)
        {
            Add(errors, "branding", "Branding settings are required.");
        }
        else
        {
            Require(settings.Branding.ApplicationTitle, "branding.applicationTitle", 120, errors);
            MaxLength(settings.Branding.ApplicationSubtitle, "branding.applicationSubtitle", 160, errors);
            ValidateOptionalFile(settings.Branding.LogoPath, "branding.logoPath", errors);
            ValidateColor(settings.Branding.PrimaryColor, "branding.primaryColor", errors);
            ValidateColor(settings.Branding.AccentColor, "branding.accentColor", errors);
            ValidateColor(settings.Branding.BackgroundColor, "branding.backgroundColor", errors);
        }

        ValidateContent(settings.ContentBlocks, errors);
        ValidateLayout(settings.Layout, errors);
        ValidateAudio(settings.Audio, errors);
        ValidateDestinations(settings, errors);
        ValidateSessionBehavior(settings.SessionBehavior, errors);
        ValidateKiosk(settings.Kiosk, errors);
        ValidateAccessibility(settings.Accessibility, errors);
        ValidateDiagnostics(settings.Diagnostics, errors);

        return new(errors);
    }

    private static void ValidateContent(IReadOnlyList<ContentBlock>? blocks, List<SettingsValidationError> errors)
    {
        if (blocks is null)
        {
            Add(errors, "contentBlocks", "Content blocks are required.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var path = $"contentBlocks[{index}]";
            if (block is null)
            {
                Add(errors, path, "Content block cannot be null.");
                continue;
            }

            Require(block.Id, $"{path}.id", 100, errors);
            if (!string.IsNullOrWhiteSpace(block.Id) && !ids.Add(block.Id))
                Add(errors, $"{path}.id", "Content block IDs must be unique.");
            MaxLength(block.Text, $"{path}.text", 4_000, errors);
            Require(block.Style, $"{path}.style", 40, errors);
        }
    }

    private static void ValidateLayout(LayoutSettings? layout, List<SettingsValidationError> errors)
    {
        if (layout is null)
        {
            Add(errors, "layout", "Layout settings are required.");
            return;
        }
        ValidateColor(layout.WaveformColor, "layout.waveformColor", errors);
        ValidateColor(layout.WaveformBackgroundColor, "layout.waveformBackgroundColor", errors);
    }

    private static void ValidateAudio(AudioSettings? audio, List<SettingsValidationError> errors)
    {
        if (audio is null)
        {
            Add(errors, "audio", "Audio settings are required.");
            return;
        }

        if (audio.RecordingFormat is null)
        {
            Add(errors, "audio.recordingFormat", "Recording format is required.");
        }
        else
        {
            if (!SampleRates.Contains(audio.RecordingFormat.SampleRate))
                Add(errors, "audio.recordingFormat.sampleRate", "Version 1 requires 48000 Hz.");
            if (audio.RecordingFormat.Channels != 1)
                Add(errors, "audio.recordingFormat.channels", "Version 1 export requires one-channel mono audio.");
            if (audio.RecordingFormat.BitsPerSample != 16)
                Add(errors, "audio.recordingFormat.bitsPerSample", "Version 1 requires 16-bit PCM.");
        }

        if (!Bitrates.Contains(audio.Mp3BitrateKbps))
            Add(errors, "audio.mp3BitrateKbps", "Choose 96, 128, 160, 192, 256, or 320 kbps.");
        ValidateDevice(audio.InputDevice, AudioDeviceFlow.Input, "audio.inputDevice", errors);
        ValidateDevice(audio.OutputDevice, AudioDeviceFlow.Output, "audio.outputDevice", errors);
        Range(audio.LowLevelThresholdDb, -90, -6, "audio.lowLevelThresholdDb", errors);
        Range(audio.ClippingThresholdDb, -12, 0, "audio.clippingThresholdDb", errors);
        if (audio.LowLevelThresholdDb >= audio.ClippingThresholdDb)
            Add(errors, "audio.lowLevelThresholdDb", "The low-level threshold must be below the clipping threshold.");
    }

    private static void ValidateDevice(
        AudioDeviceSelection? device,
        AudioDeviceFlow expectedFlow,
        string path,
        List<SettingsValidationError> errors)
    {
        if (device is null)
        {
            Add(errors, path, "Device selection is required.");
            return;
        }
        if (device.Flow != expectedFlow) Add(errors, $"{path}.flow", $"Device flow must be {expectedFlow}.");
        if (!device.UseDefault && string.IsNullOrWhiteSpace(device.EndpointId))
            Add(errors, $"{path}.endpointId", "An endpoint ID is required when Windows Default is disabled.");
        MaxLength(device.EndpointId, $"{path}.endpointId", 2_000, errors);
        MaxLength(device.FriendlyNameFallback, $"{path}.friendlyNameFallback", 300, errors);
    }

    private static void ValidateDestinations(KioskSettings settings, List<SettingsValidationError> errors)
    {
        if (settings.Destinations is null)
        {
            Add(errors, "destinations", "Destination list is required.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < settings.Destinations.Count; index++)
        {
            var destination = settings.Destinations[index];
            var path = $"destinations[{index}]";
            if (destination is null)
            {
                Add(errors, path, "Destination cannot be null.");
                continue;
            }

            Require(destination.Id, $"{path}.id", 100, errors);
            Require(destination.Label, $"{path}.label", 160, errors);
            if (!string.IsNullOrWhiteSpace(destination.Id) && !ids.Add(destination.Id))
                Add(errors, $"{path}.id", "Destination IDs must be unique.");
            if (destination.IsEnabled)
            {
                try { _ = DestinationInspector.NormalizeApprovedRoot(destination.RootPath); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    Add(errors, $"{path}.rootPath", exception.Message);
                }
            }
            Range(destination.MinimumFreeBytes, 0, 1_099_511_627_776, $"{path}.minimumFreeBytes", errors);
        }

        var enabled = settings.Destinations.Where(destination => destination is not null && destination.IsEnabled).ToArray();
        if (enabled.Length == 0) Add(errors, "destinations", "At least one export destination must be enabled.");
        if (string.IsNullOrWhiteSpace(settings.DefaultDestinationId))
        {
            Add(errors, "defaultDestinationId", "Choose a default enabled destination.");
        }
        else if (!enabled.Any(destination => string.Equals(destination.Id, settings.DefaultDestinationId, StringComparison.OrdinalIgnoreCase)))
        {
            Add(errors, "defaultDestinationId", "The default destination must refer to an enabled destination.");
        }
    }

    private static void ValidateSessionBehavior(SessionBehaviorSettings? behavior, List<SettingsValidationError> errors)
    {
        if (behavior is null)
        {
            Add(errors, "sessionBehavior", "Session behavior settings are required.");
            return;
        }

        Require(behavior.DefaultClipNameFormat, "sessionBehavior.defaultClipNameFormat", 100, errors);
        try
        {
            if (string.IsNullOrWhiteSpace(string.Format(CultureInfo.InvariantCulture, behavior.DefaultClipNameFormat, 1)))
                Add(errors, "sessionBehavior.defaultClipNameFormat", "Format must produce a clip name.");
        }
        catch (FormatException)
        {
            Add(errors, "sessionBehavior.defaultClipNameFormat", "Format must accept one numeric argument, for example 'Clip {0}'.");
        }

        Range(behavior.InactivityWarningMinutes, 0, 1_440, "sessionBehavior.inactivityWarningMinutes", errors);
        Range(behavior.RecoveryAlertDays, 1, 365, "sessionBehavior.recoveryAlertDays", errors);
        Range(behavior.SuccessfulRecoveryRetentionHours, 0, 8_760, "sessionBehavior.successfulRecoveryRetentionHours", errors);
        Range(behavior.SuccessScreenSeconds, 0, 300, "sessionBehavior.successScreenSeconds", errors);
    }

    private static void ValidateKiosk(KioskBehaviorSettings? kiosk, List<SettingsValidationError> errors)
    {
        if (kiosk is null) Add(errors, "kiosk", "Kiosk behavior settings are required.");
    }

    private static void ValidateAccessibility(AccessibilitySettings? accessibility, List<SettingsValidationError> errors)
    {
        if (accessibility is null)
        {
            Add(errors, "accessibility", "Accessibility settings are required.");
            return;
        }
        Range(accessibility.TextScale, 0.75, 2.0, "accessibility.textScale", errors);
    }

    private static void ValidateDiagnostics(DiagnosticsSettings? diagnostics, List<SettingsValidationError> errors)
    {
        if (diagnostics is null)
        {
            Add(errors, "diagnostics", "Diagnostics settings are required.");
            return;
        }
        if (!Enum.TryParse<AppLogLevel>(diagnostics.LogLevel, ignoreCase: true, out _))
            Add(errors, "diagnostics.logLevel", "Unknown log level.");
        Range(diagnostics.LogRetentionDays, 1, 365, "diagnostics.logRetentionDays", errors);
    }

    private static void ValidateOptionalFile(string? value, string path, List<SettingsValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            if (!Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(value)))
                Add(errors, path, "Logo path must be an absolute local path.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            Add(errors, path, exception.Message);
        }
    }

    private static void Require(string? value, string path, int maxLength, List<SettingsValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) Add(errors, path, "A value is required.");
        else MaxLength(value, path, maxLength, errors);
    }

    private static void MaxLength(string? value, string path, int maxLength, List<SettingsValidationError> errors)
    {
        if (value?.Length > maxLength) Add(errors, path, $"Value cannot exceed {maxLength} characters.");
    }

    private static void ValidateColor(string? value, string path, List<SettingsValidationError> errors)
    {
        if (!HexColorRegex().IsMatch(value ?? string.Empty)) Add(errors, path, "Use #RRGGBB or #AARRGGBB.");
    }

    private static void Range(double value, double minimum, double maximum, string path, List<SettingsValidationError> errors)
    {
        if (double.IsNaN(value) || value < minimum || value > maximum)
            Add(errors, path, $"Value must be from {minimum} through {maximum}.");
    }

    private static void Add(List<SettingsValidationError> errors, string path, string message) => errors.Add(new(path, message));

    [GeneratedRegex("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();
}
