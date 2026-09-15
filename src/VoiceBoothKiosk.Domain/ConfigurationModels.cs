using System.Text.Json.Serialization;

namespace VoiceBoothKiosk.Domain;

public sealed record DestinationPreset
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string RootPath { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsLocked { get; init; } = true;
    public long MinimumFreeBytes { get; init; } = 1_073_741_824;
}

public sealed record ContentBlock
{
    public required string Id { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Style { get; init; } = "Body";
    public bool IsVisible { get; init; } = true;
}

public sealed record BrandingSettings
{
    public string ApplicationTitle { get; init; } = "Voice Booth";
    public string ApplicationSubtitle { get; init; } = "Walk-up recording studio";
    // Read-only compatibility bridge for schema-1 settings. Migration moves this
    // value into welcome.supporting.1 and clears it before settings are saved.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WelcomeSupportingText { get; init; }
    public string? LogoPath { get; init; }
    public string PrimaryColor { get; init; } = "#4CC9F0";
    public string AccentColor { get; init; } = "#F0445A";
    public string BackgroundColor { get; init; } = "#08111F";
}

public sealed record LayoutSettings
{
    public ClipRailPosition ClipRailPosition { get; init; } = ClipRailPosition.Left;
    public ControlSize ControlSize { get; init; } = ControlSize.Large;
    public string WaveformColor { get; init; } = "#4CC9F0";
    public string WaveformBackgroundColor { get; init; } = "#07111F";
    public bool ShowZoomControls { get; init; } = true;
}

public sealed record AudioSettings
{
    public AudioFormat RecordingFormat { get; init; } = AudioFormat.CanonicalMono;
    public int Mp3BitrateKbps { get; init; } = 128;
    public AudioDeviceSelection InputDevice { get; init; } = AudioDeviceSelection.WindowsDefault(AudioDeviceFlow.Input);
    public AudioDeviceSelection OutputDevice { get; init; } = AudioDeviceSelection.WindowsDefault(AudioDeviceFlow.Output);
    public bool AllowPatronDeviceSelection { get; init; } = true;
    public bool AllowOutputVolumeControl { get; init; } = true;
    public double LowLevelThresholdDb { get; init; } = -40;
    public double ClippingThresholdDb { get; init; } = -1;
}

public sealed record SessionBehaviorSettings
{
    public string DefaultClipNameFormat { get; init; } = "Clip {0}";
    public bool SkipDestinationSelection { get; init; }
    public int InactivityWarningMinutes { get; init; } = 15;
    public int RecoveryAlertDays { get; init; } = 7;
    public int SuccessfulRecoveryRetentionHours { get; init; } = 24;
    public bool ConfirmNewTake { get; init; } = true;
    public bool ConfirmDeleteClip { get; init; } = true;
    public int SuccessScreenSeconds { get; init; } = 8;
}

public sealed record KioskBehaviorSettings
{
    public bool FullScreen { get; init; } = true;
    public bool AlwaysOnTop { get; init; }
    public bool HideCursorWhenIdle { get; init; } = true;
    /// <summary>
    /// Requests that Windows keep the display and system awake while the public
    /// recorder is running. Windows lock-screen and managed power policies remain authoritative.
    /// </summary>
    public bool KeepAwake { get; init; } = true;
}

public sealed record AccessibilitySettings
{
    public bool HighContrast { get; init; }
    public double TextScale { get; init; } = 1.0;
    public bool ReducedMotion { get; init; }
    public bool AudibleStatusCues { get; init; }
}

public sealed record DiagnosticsSettings
{
    public string LogLevel { get; init; } = "Information";
    public int LogRetentionDays { get; init; } = 14;
    public bool RunHardwareSelfTestOnStartup { get; init; } = true;
}

/// <summary>The three-part product version stamped into settings and session metadata.</summary>
public static class VoiceBoothRelease
{
    public static string Version
    {
        get
        {
            var version = typeof(VoiceBoothRelease).Assembly.GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string DisplayVersion => $"v{Version}";
}

/// <summary>Versioned, JSON-friendly settings shared by the kiosk and administrator app.</summary>
public sealed record KioskSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ApplicationVersion { get; init; } = VoiceBoothRelease.Version;
    public BrandingSettings Branding { get; init; } = new();
    public IReadOnlyList<ContentBlock> ContentBlocks { get; init; } = DefaultContentBlocks();
    public LayoutSettings Layout { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public IReadOnlyList<DestinationPreset> Destinations { get; init; } = [];
    public string? DefaultDestinationId { get; init; }
    public bool AllowAdministratorBrowseDestination { get; init; }
    public SessionBehaviorSettings SessionBehavior { get; init; } = new();
    public KioskBehaviorSettings Kiosk { get; init; } = new();
    public AccessibilitySettings Accessibility { get; init; } = new();
    public DiagnosticsSettings Diagnostics { get; init; } = new();

    public static KioskSettings CreateDefault() => new();

    private static IReadOnlyList<ContentBlock> DefaultContentBlocks() =>
    [
        new() { Id = "welcome.instructions", Text = "Enter a session name to begin." },
        new() { Id = "welcome.supporting.1", Text = "Name your session, then record and review as many clips as you need.", Style = "Supporting" },
        new() { Id = "welcome.supporting.2", Text = "You can pause, review, and create additional clips before saving.", Style = "Supporting" },
        new() { Id = "welcome.session-name-help", Text = "Suggested format: Name-topic", Style = "Help" },
        new() { Id = "recording.instructions", Text = "Press Record when you are ready." },
        new() { Id = "devices.help", Text = "Choose the microphone and headphones for this session.", Style = "Help" },
        new() { Id = "session.end", Text = "Review your clips and choose where to save them." },
        new() { Id = "privacy.notice", Text = "Recordings are stored locally until the session is successfully exported.", Style = "Help" }
    ];
}

/// <summary>Settings frozen into a session so a later configuration change cannot reinterpret it.</summary>
public sealed record SessionSettingsSnapshot
{
    public string ApplicationVersion { get; init; } = VoiceBoothRelease.Version;
    public AudioFormat RecordingFormat { get; init; } = AudioFormat.CanonicalMono;
    public int Mp3BitrateKbps { get; init; } = 128;
    public AudioDeviceSelection InputDevice { get; init; } = AudioDeviceSelection.WindowsDefault(AudioDeviceFlow.Input);
    public AudioDeviceSelection OutputDevice { get; init; } = AudioDeviceSelection.WindowsDefault(AudioDeviceFlow.Output);
    public string DefaultClipNameFormat { get; init; } = "Clip {0}";

    public static SessionSettingsSnapshot From(KioskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new()
        {
            ApplicationVersion = settings.ApplicationVersion,
            RecordingFormat = settings.Audio.RecordingFormat,
            Mp3BitrateKbps = settings.Audio.Mp3BitrateKbps,
            InputDevice = settings.Audio.InputDevice,
            OutputDevice = settings.Audio.OutputDevice,
            DefaultClipNameFormat = settings.SessionBehavior.DefaultClipNameFormat
        };
    }
}
