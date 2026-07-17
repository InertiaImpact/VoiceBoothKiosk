using System.Collections.ObjectModel;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Config;

public sealed class SettingsEditorViewModel
{
    public SettingsEditorViewModel(KioskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplicationVersion = settings.ApplicationVersion;
        ApplicationTitle = settings.Branding.ApplicationTitle;
        ApplicationSubtitle = settings.Branding.ApplicationSubtitle;
        LogoPath = settings.Branding.LogoPath ?? string.Empty;
        PrimaryColor = settings.Branding.PrimaryColor;
        AccentColor = settings.Branding.AccentColor;
        BackgroundColor = settings.Branding.BackgroundColor;

        ContentBlocks = new(settings.ContentBlocks.Select(ContentBlockRow.From));
        ClipRailPosition = settings.Layout.ClipRailPosition;
        ControlSize = settings.Layout.ControlSize;
        WaveformColor = settings.Layout.WaveformColor;
        WaveformBackgroundColor = settings.Layout.WaveformBackgroundColor;
        ShowZoomControls = settings.Layout.ShowZoomControls;

        SampleRate = settings.Audio.RecordingFormat.SampleRate;
        Channels = settings.Audio.RecordingFormat.Channels;
        Mp3BitrateKbps = settings.Audio.Mp3BitrateKbps;
        UseDefaultInput = settings.Audio.InputDevice.UseDefault;
        InputDeviceId = settings.Audio.InputDevice.EndpointId ?? string.Empty;
        InputDeviceName = settings.Audio.InputDevice.FriendlyNameFallback;
        UseDefaultOutput = settings.Audio.OutputDevice.UseDefault;
        OutputDeviceId = settings.Audio.OutputDevice.EndpointId ?? string.Empty;
        OutputDeviceName = settings.Audio.OutputDevice.FriendlyNameFallback;
        AllowPatronDeviceSelection = settings.Audio.AllowPatronDeviceSelection;
        AllowOutputVolumeControl = settings.Audio.AllowOutputVolumeControl;
        LowLevelThresholdDb = settings.Audio.LowLevelThresholdDb;
        ClippingThresholdDb = settings.Audio.ClippingThresholdDb;

        Destinations = new(settings.Destinations.Select(destination =>
            DestinationRow.From(destination, string.Equals(destination.Id, settings.DefaultDestinationId, StringComparison.OrdinalIgnoreCase))));
        AllowAdministratorBrowseDestination = settings.AllowAdministratorBrowseDestination;

        DefaultClipNameFormat = settings.SessionBehavior.DefaultClipNameFormat;
        InactivityWarningMinutes = settings.SessionBehavior.InactivityWarningMinutes;
        RecoveryAlertDays = settings.SessionBehavior.RecoveryAlertDays;
        SuccessfulRecoveryRetentionHours = settings.SessionBehavior.SuccessfulRecoveryRetentionHours;
        ConfirmNewTake = settings.SessionBehavior.ConfirmNewTake;
        ConfirmDeleteClip = settings.SessionBehavior.ConfirmDeleteClip;
        SuccessScreenSeconds = settings.SessionBehavior.SuccessScreenSeconds;

        FullScreen = settings.Kiosk.FullScreen;
        AlwaysOnTop = settings.Kiosk.AlwaysOnTop;
        AllowClose = settings.Kiosk.AllowClose;
        HideCursorWhenIdle = settings.Kiosk.HideCursorWhenIdle;

        HighContrast = settings.Accessibility.HighContrast;
        TextScale = settings.Accessibility.TextScale;
        ReducedMotion = settings.Accessibility.ReducedMotion;
        AudibleStatusCues = settings.Accessibility.AudibleStatusCues;

        LogLevel = settings.Diagnostics.LogLevel;
        LogRetentionDays = settings.Diagnostics.LogRetentionDays;
        RunHardwareSelfTestOnStartup = settings.Diagnostics.RunHardwareSelfTestOnStartup;
    }

    public IReadOnlyList<int> SampleRates { get; } = [48_000];
    public IReadOnlyList<int> ChannelChoices { get; } = [1];
    public IReadOnlyList<int> BitrateChoices { get; } = [96, 128, 160, 192, 256, 320];
    public IReadOnlyList<ClipRailPosition> ClipRailChoices { get; } = Enum.GetValues<ClipRailPosition>();
    public IReadOnlyList<ControlSize> ControlSizeChoices { get; } = Enum.GetValues<ControlSize>();
    public IReadOnlyList<string> LogLevelChoices { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    public string ApplicationVersion { get; set; }
    public string ApplicationTitle { get; set; }
    public string ApplicationSubtitle { get; set; }
    public string LogoPath { get; set; }
    public string PrimaryColor { get; set; }
    public string AccentColor { get; set; }
    public string BackgroundColor { get; set; }
    public ObservableCollection<ContentBlockRow> ContentBlocks { get; }
    public ContentBlockRow? SelectedContentBlock { get; set; }

    public ClipRailPosition ClipRailPosition { get; set; }
    public ControlSize ControlSize { get; set; }
    public string WaveformColor { get; set; }
    public string WaveformBackgroundColor { get; set; }
    public bool ShowZoomControls { get; set; }

    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int Mp3BitrateKbps { get; set; }
    public bool UseDefaultInput { get; set; }
    public string InputDeviceId { get; set; }
    public string InputDeviceName { get; set; }
    public bool UseDefaultOutput { get; set; }
    public string OutputDeviceId { get; set; }
    public string OutputDeviceName { get; set; }
    public bool AllowPatronDeviceSelection { get; set; }
    public bool AllowOutputVolumeControl { get; set; }
    public double LowLevelThresholdDb { get; set; }
    public double ClippingThresholdDb { get; set; }

    public ObservableCollection<DestinationRow> Destinations { get; }
    public DestinationRow? SelectedDestination { get; set; }
    public bool AllowAdministratorBrowseDestination { get; set; }

    public string DefaultClipNameFormat { get; set; }
    public int InactivityWarningMinutes { get; set; }
    public int RecoveryAlertDays { get; set; }
    public int SuccessfulRecoveryRetentionHours { get; set; }
    public bool ConfirmNewTake { get; set; }
    public bool ConfirmDeleteClip { get; set; }
    public int SuccessScreenSeconds { get; set; }

    public bool FullScreen { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool AllowClose { get; set; }
    public bool HideCursorWhenIdle { get; set; }

    public bool HighContrast { get; set; }
    public double TextScale { get; set; }
    public bool ReducedMotion { get; set; }
    public bool AudibleStatusCues { get; set; }

    public string LogLevel { get; set; }
    public int LogRetentionDays { get; set; }
    public bool RunHardwareSelfTestOnStartup { get; set; }

    public KioskSettings ToSettings()
    {
        var defaultDestination = Destinations.FirstOrDefault(destination => destination.IsDefault);
        return new KioskSettings
        {
            SchemaVersion = KioskSettings.CurrentSchemaVersion,
            ApplicationVersion = string.IsNullOrWhiteSpace(ApplicationVersion) ? "1.0.0" : ApplicationVersion.Trim(),
            Branding = new BrandingSettings
            {
                ApplicationTitle = ApplicationTitle,
                ApplicationSubtitle = ApplicationSubtitle?.Trim() ?? string.Empty,
                LogoPath = string.IsNullOrWhiteSpace(LogoPath) ? null : LogoPath.Trim(),
                PrimaryColor = PrimaryColor,
                AccentColor = AccentColor,
                BackgroundColor = BackgroundColor
            },
            ContentBlocks = ContentBlocks.Select(row => row.ToDomain()).ToArray(),
            Layout = new LayoutSettings
            {
                ClipRailPosition = ClipRailPosition,
                ControlSize = ControlSize,
                WaveformColor = WaveformColor,
                WaveformBackgroundColor = WaveformBackgroundColor,
                ShowZoomControls = ShowZoomControls
            },
            Audio = new AudioSettings
            {
                RecordingFormat = new AudioFormat(SampleRate, Channels, 16),
                Mp3BitrateKbps = Mp3BitrateKbps,
                InputDevice = new AudioDeviceSelection
                {
                    EndpointId = string.IsNullOrWhiteSpace(InputDeviceId) ? null : InputDeviceId.Trim(),
                    FriendlyNameFallback = InputDeviceName?.Trim() ?? string.Empty,
                    Flow = AudioDeviceFlow.Input,
                    UseDefault = UseDefaultInput
                },
                OutputDevice = new AudioDeviceSelection
                {
                    EndpointId = string.IsNullOrWhiteSpace(OutputDeviceId) ? null : OutputDeviceId.Trim(),
                    FriendlyNameFallback = OutputDeviceName?.Trim() ?? string.Empty,
                    Flow = AudioDeviceFlow.Output,
                    UseDefault = UseDefaultOutput
                },
                AllowPatronDeviceSelection = AllowPatronDeviceSelection,
                AllowOutputVolumeControl = AllowOutputVolumeControl,
                LowLevelThresholdDb = LowLevelThresholdDb,
                ClippingThresholdDb = ClippingThresholdDb
            },
            Destinations = Destinations.Select(row => row.ToDomain()).ToArray(),
            DefaultDestinationId = defaultDestination?.Id,
            AllowAdministratorBrowseDestination = AllowAdministratorBrowseDestination,
            SessionBehavior = new SessionBehaviorSettings
            {
                DefaultClipNameFormat = DefaultClipNameFormat,
                InactivityWarningMinutes = InactivityWarningMinutes,
                RecoveryAlertDays = RecoveryAlertDays,
                SuccessfulRecoveryRetentionHours = SuccessfulRecoveryRetentionHours,
                ConfirmNewTake = ConfirmNewTake,
                ConfirmDeleteClip = ConfirmDeleteClip,
                SuccessScreenSeconds = SuccessScreenSeconds
            },
            Kiosk = new KioskBehaviorSettings
            {
                FullScreen = FullScreen,
                AlwaysOnTop = AlwaysOnTop,
                AllowClose = AllowClose,
                HideCursorWhenIdle = HideCursorWhenIdle
            },
            Accessibility = new AccessibilitySettings
            {
                HighContrast = HighContrast,
                TextScale = TextScale,
                ReducedMotion = ReducedMotion,
                AudibleStatusCues = AudibleStatusCues
            },
            Diagnostics = new DiagnosticsSettings
            {
                LogLevel = LogLevel,
                LogRetentionDays = LogRetentionDays,
                RunHardwareSelfTestOnStartup = RunHardwareSelfTestOnStartup
            }
        };
    }

    public void SetDefaultDestination(DestinationRow selected)
    {
        foreach (var destination in Destinations) destination.IsDefault = ReferenceEquals(destination, selected);
    }
}

public sealed class ContentBlockRow
{
    public string Id { get; set; } = "recording.custom";
    public string Text { get; set; } = string.Empty;
    public string Style { get; set; } = "Body";
    public bool IsVisible { get; set; } = true;

    public static ContentBlockRow From(ContentBlock source) => new()
    {
        Id = source.Id,
        Text = source.Text,
        Style = source.Style,
        IsVisible = source.IsVisible
    };

    public ContentBlock ToDomain() => new() { Id = Id, Text = Text, Style = Style, IsVisible = IsVisible };
}

public sealed class DestinationRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "New destination";
    public string RootPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool IsLocked { get; set; } = true;
    public bool IsDefault { get; set; }
    public long MinimumFreeMegabytes { get; set; } = 1024;

    public static DestinationRow From(DestinationPreset source, bool isDefault) => new()
    {
        Id = source.Id,
        Label = source.Label,
        RootPath = source.RootPath,
        IsEnabled = source.IsEnabled,
        IsLocked = source.IsLocked,
        IsDefault = isDefault,
        MinimumFreeMegabytes = source.MinimumFreeBytes / (1024 * 1024)
    };

    public DestinationPreset ToDomain() => new()
    {
        Id = Id?.Trim() ?? string.Empty,
        Label = Label?.Trim() ?? string.Empty,
        RootPath = RootPath?.Trim() ?? string.Empty,
        IsEnabled = IsEnabled,
        IsLocked = IsLocked,
        MinimumFreeBytes = checked(Math.Max(0, MinimumFreeMegabytes) * 1024 * 1024)
    };
}
