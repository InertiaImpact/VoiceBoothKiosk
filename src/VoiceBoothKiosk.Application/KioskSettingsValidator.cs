using System.Text.RegularExpressions;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Application;

/// <summary>Shared deterministic validation for JSON-loaded configuration.</summary>
public static partial class KioskSettingsValidator
{
    private static readonly HashSet<int> SupportedMp3Bitrates = [96, 128, 160, 192, 256, 320];

    public static SettingsValidationResult Validate(KioskSettings? settings)
    {
        var errors = new List<SettingsValidationError>();
        if (settings is null)
        {
            return new([new("$", "Settings are required.")]);
        }

        if (settings.SchemaVersion != KioskSettings.CurrentSchemaVersion)
        {
            errors.Add(new("schemaVersion", $"Expected schema version {KioskSettings.CurrentSchemaVersion}."));
        }

        if (string.IsNullOrWhiteSpace(settings.ApplicationVersion))
        {
            errors.Add(new("applicationVersion", "Application version is required."));
        }

        if (string.IsNullOrWhiteSpace(settings.Branding.ApplicationTitle))
        {
            errors.Add(new("branding.applicationTitle", "Application title is required."));
        }
        else if (settings.Branding.ApplicationTitle.Length > 120)
        {
            errors.Add(new("branding.applicationTitle", "Application title cannot exceed 120 characters."));
        }
        if (settings.Branding.ApplicationSubtitle?.Length > 160)
        {
            errors.Add(new("branding.applicationSubtitle", "Application subtitle cannot exceed 160 characters."));
        }

        ValidateColor(settings.Branding.PrimaryColor, "branding.primaryColor", errors);
        ValidateColor(settings.Branding.AccentColor, "branding.accentColor", errors);
        ValidateColor(settings.Branding.BackgroundColor, "branding.backgroundColor", errors);
        ValidateColor(settings.Layout.WaveformColor, "layout.waveformColor", errors);
        ValidateColor(settings.Layout.WaveformBackgroundColor, "layout.waveformBackgroundColor", errors);

        var format = settings.Audio.RecordingFormat;
        if (format.SampleRate != 48_000)
        {
            errors.Add(new("audio.recordingFormat.sampleRate", "Version 1 requires a 48000 Hz recording format."));
        }
        if (format.Channels != 1)
        {
            errors.Add(new("audio.recordingFormat.channels", "Version 1 requires one-channel mono audio."));
        }
        if (format.BitsPerSample != 16)
        {
            errors.Add(new("audio.recordingFormat.bitsPerSample", "Version 1 requires 16-bit PCM."));
        }
        if (!SupportedMp3Bitrates.Contains(settings.Audio.Mp3BitrateKbps))
        {
            errors.Add(new("audio.mp3BitrateKbps", "Choose a supported MP3 bitrate: 96, 128, 160, 192, 256, or 320 kbps."));
        }
        ValidateDevice(settings.Audio.InputDevice, AudioDeviceFlow.Input, "audio.inputDevice", errors);
        ValidateDevice(settings.Audio.OutputDevice, AudioDeviceFlow.Output, "audio.outputDevice", errors);
        if (settings.Audio.LowLevelThresholdDb >= settings.Audio.ClippingThresholdDb)
        {
            errors.Add(new("audio.lowLevelThresholdDb", "The low-level threshold must be below the clipping threshold."));
        }

        var destinationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in settings.Destinations ?? [])
        {
            var path = $"destinations[{destination.Id}]";
            if (string.IsNullOrWhiteSpace(destination.Id))
            {
                errors.Add(new("destinations", "Every destination requires a stable ID."));
            }
            else if (!destinationIds.Add(destination.Id))
            {
                errors.Add(new(path, "Destination IDs must be unique."));
            }
            if (string.IsNullOrWhiteSpace(destination.Label))
            {
                errors.Add(new($"{path}.label", "A destination label is required."));
            }
            if (string.IsNullOrWhiteSpace(destination.RootPath) || !Path.IsPathFullyQualified(destination.RootPath))
            {
                errors.Add(new($"{path}.rootPath", "The destination must be a fully qualified local or UNC path."));
            }
            if (destination.MinimumFreeBytes < 0)
            {
                errors.Add(new($"{path}.minimumFreeBytes", "Minimum free space cannot be negative."));
            }
        }
        if (settings.DefaultDestinationId is not null &&
            !(settings.Destinations ?? []).Any(destination => destination.IsEnabled &&
                string.Equals(destination.Id, settings.DefaultDestinationId, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new("defaultDestinationId", "The default destination must refer to an enabled destination."));
        }

        var contentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in settings.ContentBlocks ?? [])
        {
            if (string.IsNullOrWhiteSpace(block.Id))
            {
                errors.Add(new("contentBlocks", "Every content block requires an ID."));
            }
            else if (!contentIds.Add(block.Id))
            {
                errors.Add(new($"contentBlocks[{block.Id}]", "Content block IDs must be unique."));
            }
        }

        if (settings.Accessibility.TextScale is < 0.75 or > 2.0)
        {
            errors.Add(new("accessibility.textScale", "Text scale must be between 0.75 and 2.0."));
        }
        if (settings.SessionBehavior.InactivityWarningMinutes < 0)
        {
            errors.Add(new("sessionBehavior.inactivityWarningMinutes", "Inactivity warning time cannot be negative."));
        }
        if (settings.SessionBehavior.RecoveryAlertDays < 1)
        {
            errors.Add(new("sessionBehavior.recoveryAlertDays", "Recovery alert retention must be at least one day."));
        }
        if (settings.SessionBehavior.SuccessfulRecoveryRetentionHours < 0)
        {
            errors.Add(new("sessionBehavior.successfulRecoveryRetentionHours", "Successful-session retention cannot be negative."));
        }
        if (settings.SessionBehavior.SuccessScreenSeconds < 0)
        {
            errors.Add(new("sessionBehavior.successScreenSeconds", "Success-screen delay cannot be negative."));
        }
        if (settings.Diagnostics.LogRetentionDays < 1)
        {
            errors.Add(new("diagnostics.logRetentionDays", "Log retention must be at least one day."));
        }

        return new(errors);
    }

    private static void ValidateDevice(AudioDeviceSelection selection, AudioDeviceFlow expectedFlow, string path, ICollection<SettingsValidationError> errors)
    {
        if (selection.Flow != expectedFlow)
        {
            errors.Add(new($"{path}.flow", $"This must be an {expectedFlow} selection."));
        }
        if (!selection.UseDefault && string.IsNullOrWhiteSpace(selection.EndpointId))
        {
            errors.Add(new($"{path}.endpointId", "A non-default device requires a stable endpoint ID."));
        }
    }

    private static void ValidateColor(string value, string path, ICollection<SettingsValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !HexColor().IsMatch(value))
        {
            errors.Add(new(path, "Use #RRGGBB or #AARRGGBB color notation."));
        }
    }

    [GeneratedRegex("^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
}
