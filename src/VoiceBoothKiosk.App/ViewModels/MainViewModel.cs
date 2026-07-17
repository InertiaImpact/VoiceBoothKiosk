using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoiceBoothKiosk.App.Services;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IMainWindowActions, IAsyncDisposable
{
    private const string WindowsDefaultDeviceId = "__windows_default__";
    private const int MaximumWaveformPeaks = 4096;
    private const int WaveformCompactionTarget = 3072;
    private const long MinimumLocalFreeBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan RecordingUiInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan InactivityCountdown = TimeSpan.FromSeconds(60);

    private readonly SessionCoordinator _coordinator;
    private readonly IAudioDeviceService _deviceService;
    private readonly IWorkingStorage _workingStorage;
    private readonly IExportService _exportService;
    private readonly IUserPromptService _prompts;
    private readonly IConfigurationEditorLauncher _configurationEditorLauncher;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _recordingUiTimer;
    private readonly object _pendingRecordingGate = new();
    private readonly List<WaveformPeak> _pendingRecordingPeaks = [];
    private SessionCoordinatorState _state;
    private SessionCoordinatorState? _pendingRecordingState;
    private IReadOnlyList<double> _waveformPeaks = [];
    private string _sessionNameInput = string.Empty;
    private ClipItemViewModel? _selectedClip;
    private DeviceOptionViewModel? _selectedInputDevice;
    private DeviceOptionViewModel? _selectedOutputDevice;
    private DestinationOptionViewModel? _selectedDestination;
    private bool _isDevicePanelOpen;
    private bool _isEndSessionPanelOpen;
    private bool _applyingState;
    private bool _applyingDevices;
    private bool _applyingOutputVolume;
    private double _outputVolume = 1;
    private CancellationTokenSource? _volumeChangeCancellation;
    private CancellationTokenSource? _inputTestCancellation;
    private Task<AudioDeviceTestResult>? _inputTestTask;
    private Task? _inputTestCompletionTask;
    private bool _isInputTestActive;
    private bool _isInputTestStopping;
    private bool? _inputTestSucceeded;
    private CancellationTokenSource? _outputTestCancellation;
    private Task<AudioDeviceTestResult>? _outputTestTask;
    private bool _isOutputTestActive;
    private bool? _outputTestSucceeded;
    private string _inputTestMessage = "Select Start Mic Test to watch the live microphone level.";
    private string _outputTestMessage = "Select Test Speakers to hear a two-note tone.";
    private string _readinessMessage = "Checking audio devices…";
    private bool _isReady;
    private bool _hasAvailableInput;
    private bool _hasAdequateLocalStorage;
    private bool _hasAvailableEncoder;
    private string? _exportPreflightMessage = "Checking the MP3 encoder…";
    private string _freeSpaceText = "Checking local storage…";
    private string _statusMessage = "Starting…";
    private double _displayInputLevel;
    private TimeSpan _displayPlayhead;
    private Guid? _loadedWaveformClipId;
    private long _lastAppliedStateVersion = -1;
    private CancellationTokenSource? _waveformLoadCancellation;
    private DateTimeOffset _lastUserActivityUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _inactivityWarningStartedUtc;
    private DateTimeOffset? _successShownAtUtc;
    private bool _isInactivityWarningVisible;
    private bool _inactivityResetInProgress;
    private bool _successResetInProgress;
    private bool _cursorHidden;
    private string _inactivityCountdownText = "This session will be secured in 60 seconds.";
    private ImageSource? _logoSource;
    private KioskSettings? _appliedVisualSettings;
    private bool _configurationEditorOpen;
    private bool _disposed;

    public MainViewModel(
        SessionCoordinator coordinator,
        IAudioDeviceService deviceService,
        IWorkingStorage workingStorage,
        IExportService exportService,
        IUserPromptService prompts,
        IConfigurationEditorLauncher configurationEditorLauncher)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
        _workingStorage = workingStorage ?? throw new ArgumentNullException(nameof(workingStorage));
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _configurationEditorLauncher = configurationEditorLauncher ?? throw new ArgumentNullException(nameof(configurationEditorLauncher));
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _state = coordinator.State;

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _coordinator.OperationFailed += OnCoordinatorOperationFailed;
        _deviceService.DevicesChanged += OnDevicesChanged;

        _sessionTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnSessionTimerTick, _dispatcher);
        _recordingUiTimer = new DispatcherTimer(RecordingUiInterval, DispatcherPriority.Background, OnRecordingUiTimerTick, _dispatcher);

        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, CanStartSession);
        RecordCommand = new AsyncRelayCommand(RecordPauseAsync, CanRecordPause);
        PlayPauseCommand = new AsyncRelayCommand(PlayPauseAsync, CanPlayPause);
        ReturnToStartCommand = new AsyncRelayCommand(ReturnToStartAsync, () => SelectedDuration > TimeSpan.Zero && !IsRecording);
        NewClipCommand = new AsyncRelayCommand(NewClipAsync, () => _state.Capabilities.CanEditClips);
        NewTakeCommand = new AsyncRelayCommand(NewTakeAsync, () => _state.Capabilities.CanNewTake);
        UndoCommand = new AsyncRelayCommand(
            () => ExecuteSafeAsync(() => _coordinator.UndoAsync()),
            () => _state.Capabilities.CanUndo);
        DeleteClipCommand = new AsyncRelayCommand<ClipItemViewModel>(DeleteClipAsync, clip => clip is not null && _state.Capabilities.CanEditClips);
        BeginRenameClipCommand = new AsyncRelayCommand<ClipItemViewModel>(BeginRenameClipAsync, clip => clip is not null && _state.Capabilities.CanEditClips);
        OpenDevicesCommand = new AsyncRelayCommand(OpenDevicesAsync, () => _state.Capabilities.CanChangeDevices);
        CloseDevicesCommand = new RelayCommand(CloseDevices);
        TestInputCommand = new AsyncRelayCommand(
            ToggleInputTestAsync,
            CanToggleInputTest);
        TestOutputCommand = new AsyncRelayCommand(
            TestOutputAsync,
            () => SelectedOutputDevice?.IsAvailable == true && _state.Capabilities.CanChangeDevices && !IsInputTestActive);

        OpenEndSessionCommand = new AsyncRelayCommand(OpenEndSessionAsync, CanOpenEndSession);
        CloseEndSessionCommand = new RelayCommand(() => IsEndSessionPanelOpen = false, () => !IsBusy);
        ExportAndEndCommand = new AsyncRelayCommand(ExportAndEndAsync,
            () => IsEndSessionPanelOpen && !IsBusy && (!HasRecordedAudio || SelectedDestination is not null));
        FinishSuccessCommand = new AsyncRelayCommand(FinishSuccessAsync, () => IsSuccessVisible);
        ContinueSessionCommand = new RelayCommand(NotifyUserActivity, () => IsInactivityWarningVisible);
    }

    public event EventHandler? ClipNameFocusRequested;

    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];
    public IReadOnlyList<double> WaveformPeaks => _waveformPeaks;
    public ObservableCollection<DeviceOptionViewModel> InputDevices { get; } = [];
    public ObservableCollection<DeviceOptionViewModel> OutputDevices { get; } = [];
    public ObservableCollection<DestinationOptionViewModel> Destinations { get; } = [];
    public ObservableCollection<string> ExportFilePreviews { get; } = [];

    public ICommand StartSessionCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand ReturnToStartCommand { get; }
    public ICommand NewClipCommand { get; }
    public ICommand NewTakeCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand DeleteClipCommand { get; }
    public ICommand BeginRenameClipCommand { get; }
    public ICommand OpenDevicesCommand { get; }
    public ICommand CloseDevicesCommand { get; }
    public ICommand TestInputCommand { get; }
    public ICommand TestOutputCommand { get; }
    public ICommand OpenEndSessionCommand { get; }
    public ICommand CloseEndSessionCommand { get; }
    public ICommand ExportAndEndCommand { get; }
    public ICommand FinishSuccessCommand { get; }
    public ICommand ContinueSessionCommand { get; }

    public string ApplicationTitle => _state.Settings.Branding.ApplicationTitle;
    public string ApplicationSubtitle => _state.Settings.Branding.ApplicationSubtitle;
    public string WelcomeInstructions => Content("welcome.instructions", "Enter a session name, then record one or more clips.");
    public string SessionNameHelp => Content("welcome.session-name-help", "Suggested format: Name-topic");
    public string RecordingInstructions => Content("recording.instructions", "Press Record when you are ready.");
    public string PrivacyNotice => Content("privacy.notice", "Recordings are held locally until this session is successfully saved.");
    public string DeviceInstructions => Content("devices.help", "Choose the microphone and headphones for this session.");
    public string EndSessionInstructions => Content("session.end", "Review your clips and choose where to save them.");
    public string EndSessionPanelInstructions => HasRecordedAudio
        ? EndSessionInstructions
        : "No audio has been recorded. Ending now will return to the welcome screen without creating any files or folders.";
    public string EndSessionPrimaryButtonText => HasRecordedAudio
        ? "Save MP3s + Raw WAVs & End Session"
        : "End Empty Session";
    public IReadOnlyList<ContentBlock> WelcomeExtraContent => ExtraContent("welcome");
    public IReadOnlyList<ContentBlock> RecordingExtraContent => ExtraContent("recording");
    public IReadOnlyList<ContentBlock> DeviceExtraContent => ExtraContent("devices");
    public IReadOnlyList<ContentBlock> EndSessionExtraContent => ExtraContent("session");
    public ImageSource? LogoSource => _logoSource;
    public bool HasLogo => LogoSource is not null;
    public bool AllowDeviceSelection => _state.Settings.Audio.AllowPatronDeviceSelection;
    public bool AllowOutputVolumeControl => _state.Settings.Audio.AllowOutputVolumeControl;
    public bool ShowHoldToExit => _state.Settings.Kiosk.AllowClose ||
                                  HasCommandLineSwitch("--windowed") ||
                                  HasCommandLineSwitch("--supervised-kiosk");
    public bool FullScreen => !HasCommandLineSwitch("--windowed") &&
                              (_state.Settings.Kiosk.FullScreen || HasCommandLineSwitch("--supervised-kiosk"));
    public bool AlwaysOnTop => _state.Settings.Kiosk.AlwaysOnTop || HasCommandLineSwitch("--supervised-kiosk");
    public bool CanOpenConfiguration => (_state.Mode is CoordinatorMode.Welcome or CoordinatorMode.Ready) &&
                                        !_configurationEditorOpen && !IsBusy;
    public Cursor KioskCursor => _cursorHidden ? Cursors.None : Cursors.Arrow;
    public double TextScale => _state.Settings.Accessibility.TextScale;
    public double ControlButtonMinHeight => _state.Settings.Layout.ControlSize switch
    {
        ControlSize.Compact => 42,
        ControlSize.Standard => 50,
        _ => 58
    };
    private double ClipRailWidth => _state.Settings.Layout.ControlSize switch
    {
        ControlSize.Compact => 270,
        ControlSize.Standard => 310,
        _ => 350
    };
    public int ClipRailColumn => _state.Settings.Layout.ClipRailPosition == ClipRailPosition.Left ? 0 : 2;
    public int MainWorkspaceColumn => _state.Settings.Layout.ClipRailPosition == ClipRailPosition.Left ? 2 : 0;
    public System.Windows.GridLength LeftWorkspaceWidth => _state.Settings.Layout.ClipRailPosition == ClipRailPosition.Left
        ? new(ClipRailWidth)
        : new(1, System.Windows.GridUnitType.Star);
    public System.Windows.GridLength RightWorkspaceWidth => _state.Settings.Layout.ClipRailPosition == ClipRailPosition.Left
        ? new(1, System.Windows.GridUnitType.Star)
        : new(ClipRailWidth);
    public Brush WaveformBrush => ParseBrush(_state.Settings.Layout.WaveformColor, Color.FromRgb(76, 201, 240));
    public Brush WaveformBackgroundBrush => ParseBrush(_state.Settings.Layout.WaveformBackgroundColor, Color.FromRgb(7, 17, 31));
    public Brush RecordingWaveformBrush => ParseBrush(_state.Settings.Branding.AccentColor, Color.FromRgb(240, 68, 90));

    public string SessionNameInput
    {
        get => _sessionNameInput;
        set
        {
            if (!SetProperty(ref _sessionNameInput, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSessionNamePreview));
            OnPropertyChanged(nameof(SessionFolderPreview));
            NotifyCommandStates();
        }
    }

    public bool HasSessionNamePreview => !string.IsNullOrWhiteSpace(SessionNameInput);
    public string SessionFolderPreview => HasSessionNamePreview
        ? $"{FileNameSanitizer.ToSafeComponent(SessionNameInput, "Session")}_{DateTimeOffset.Now:MMddyyyy}"
        : string.Empty;

    public bool IsSessionActive => _state.Session is not null && _state.Mode is not CoordinatorMode.Welcome and not CoordinatorMode.RecoveryAvailable;
    public string CurrentSessionName => _state.Session?.DisplayName ?? string.Empty;
    public string SessionElapsedText
    {
        get
        {
            var elapsed = _state.Session is null ? TimeSpan.Zero : DateTimeOffset.Now - _state.Session.StartedAtLocal;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            return $"Session {FormatDuration(elapsed)}";
        }
    }

    public ClipItemViewModel? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (!SetProperty(ref _selectedClip, value) || _applyingState || value is null || value.Id == _state.SelectedClip?.Id)
            {
                return;
            }
            _ = ExecuteSafeAsync(() => _coordinator.SelectClipAsync(value.Id));
        }
    }

    public bool CanEditClipName => _state.Capabilities.CanEditClips && SelectedClip is not null;
    public string ClipCountText => $"{Clips.Count} {(Clips.Count == 1 ? "clip" : "clips")} in this session";
    public TimeSpan SelectedDuration => (_state.SelectedClip?.Duration ?? TimeSpan.Zero)
        + (_state.Mode == CoordinatorMode.Recording ? _state.RecordingPosition : TimeSpan.Zero);
    public string SelectedDurationText => FormatDuration(SelectedDuration);
    public string SessionRecordedDurationText => FormatDuration(_state.Session?.TotalDuration ?? TimeSpan.Zero);
    public string ExportClipCountText
    {
        get
        {
            var count = _state.Session?.ActiveClips.Count(clip => clip.HasAudio) ?? 0;
            return $"{count} {(count == 1 ? "clip" : "clips")} / {FormatDuration(_state.Session?.TotalDuration ?? TimeSpan.Zero)}";
        }
    }
    public bool HasRecordedAudio => _state.Session?.ActiveClips.Any(static clip => clip.HasAudio) == true;

    public bool IsRecording => _state.Mode == CoordinatorMode.Recording;
    public bool CanUndo => _state.Capabilities.CanUndo;
    public bool IsPlaying => _state.Mode == CoordinatorMode.Playing;
    public TimeSpan Playhead => IsRecording
        ? (_state.SelectedClip?.Duration ?? TimeSpan.Zero) + _state.RecordingPosition
        : _displayPlayhead;
    public double InputLevel => IsRecording ? Math.Clamp(_state.InputPeakLevel, 0, 1) : _displayInputLevel;
    public string InputLevelText => InputLevel <= 0.00001 ? "-∞ dB" : $"{20 * Math.Log10(InputLevel):0} dB";
    private double InputLevelDb => InputLevel <= 0.00001 ? double.NegativeInfinity : 20 * Math.Log10(InputLevel);
    private bool IsInputMeterActive => IsRecording || IsInputTestActive;
    public Brush InputLevelBrush => InputLevelDb >= _state.Settings.Audio.ClippingThresholdDb
        ? Brushes.OrangeRed
        : InputLevelDb < _state.Settings.Audio.LowLevelThresholdDb && IsInputMeterActive
            ? Brushes.Gold
            : Brushes.MediumSpringGreen;
    public string InputLevelStatus => !IsInputMeterActive
        ? string.Empty
        : InputLevelDb >= _state.Settings.Audio.ClippingThresholdDb
            ? "Input is clipping — lower the microphone level or move back."
            : InputLevelDb < _state.Settings.Audio.LowLevelThresholdDb
                ? "Input level is low — move closer to the microphone."
                : "Input level is good.";
    public string RecordButtonText => IsRecording
        ? "Ⅱ  Pause Recording"
        : _state.SelectedClip?.HasAudio == true ? "●  Resume Recording" : "●  Record";
    public string PlayButtonText => _state.Mode switch
    {
        CoordinatorMode.Playing or CoordinatorMode.Paused => "Ⅱ  Pause",
        _ => "▶  Play"
    };
    public string TransportStatus => _state.Mode switch
    {
        CoordinatorMode.Recording => "RECORDING",
        CoordinatorMode.Playing => "PLAYING",
        CoordinatorMode.Paused => "PAUSED",
        CoordinatorMode.Exporting => "SAVING",
        _ => "READY"
    };
    public Brush StatusIndicatorBrush => _state.Mode switch
    {
        CoordinatorMode.Recording => Brushes.OrangeRed,
        CoordinatorMode.Playing or CoordinatorMode.Paused => Brushes.DeepSkyBlue,
        CoordinatorMode.Exporting => Brushes.Gold,
        _ => Brushes.MediumSpringGreen
    };
    public string ContextMessage => _state.StatusMessage ?? "Ready.";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }
    public string AutosaveMessage => _state.Mode == CoordinatorMode.Recording ? "Recording safely to local storage…" : "All changes saved locally";
    public Brush AutosaveBrush => _state.Mode == CoordinatorMode.Recording ? Brushes.OrangeRed : Brushes.MediumSpringGreen;
    public string FreeSpaceText
    {
        get => _freeSpaceText;
        private set => SetProperty(ref _freeSpaceText, value);
    }

    public string ReadinessMessage
    {
        get => _readinessMessage;
        private set => SetProperty(ref _readinessMessage, value);
    }
    public Brush ReadinessBrush => _isReady ? Brushes.MediumSpringGreen : Brushes.Gold;

    public bool IsDevicePanelOpen
    {
        get => _isDevicePanelOpen;
        set
        {
            if (SetProperty(ref _isDevicePanelOpen, value) && !value)
            {
                RequestStopInputTest("Microphone test stopped.");
                RequestStopOutputTest();
            }
        }
    }
    public DeviceOptionViewModel? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (!SetProperty(ref _selectedInputDevice, value) || _applyingDevices || value is null)
            {
                return;
            }
            RequestStopInputTest("Microphone changed. Select Start Mic Test when ready.");
            _hasAvailableInput = value.IsDefault
                ? InputDevices.Any(device => !device.IsDefault && device.IsAvailable)
                : value.IsAvailable;
            UpdateReadiness();
            _ = ExecuteSafeAsync(() => _coordinator.SelectInputDeviceAsync(ToSelection(value, AudioDeviceFlow.Input)));
        }
    }
    public DeviceOptionViewModel? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value) || _applyingDevices || value is null)
            {
                return;
            }
            RequestStopOutputTest();
            _ = ExecuteSafeAsync(() => _coordinator.SelectOutputDeviceAsync(ToSelection(value, AudioDeviceFlow.Output)));
            _ = ExecuteSafeAsync(LoadOutputVolumeAsync, showDialog: false);
        }
    }
    public double OutputVolume
    {
        get => _outputVolume;
        set
        {
            if (!SetProperty(ref _outputVolume, Math.Clamp(value, 0, 1)) || _applyingOutputVolume)
            {
                return;
            }
            OnPropertyChanged(nameof(OutputVolumeText));
            _ = DebounceOutputVolumeAsync();
        }
    }
    public string OutputVolumeText => $"{OutputVolume:P0}";
    public bool IsInputTestActive
    {
        get => _isInputTestActive;
        private set
        {
            if (!SetProperty(ref _isInputTestActive, value))
            {
                return;
            }
            OnPropertyChanged(nameof(InputTestButtonText));
            OnPropertyChanged(nameof(InputTestStatusBrush));
            OnPropertyChanged(nameof(InputLevelBrush));
            OnPropertyChanged(nameof(InputLevelStatus));
            NotifyCommandStates();
        }
    }
    public string InputTestButtonText => _isInputTestStopping
        ? "Stopping…"
        : IsInputTestActive ? "■  Stop Mic Test" : "●  Start Mic Test";
    public Brush InputTestStatusBrush => IsInputTestActive
        ? Brushes.DeepSkyBlue
        : _inputTestSucceeded switch
        {
            true => Brushes.MediumSpringGreen,
            false => Brushes.OrangeRed,
            _ => Brushes.LightSlateGray,
        };
    public string InputTestMessage
    {
        get => _inputTestMessage;
        private set => SetProperty(ref _inputTestMessage, value);
    }
    public bool IsOutputTestActive
    {
        get => _isOutputTestActive;
        private set
        {
            if (!SetProperty(ref _isOutputTestActive, value))
            {
                return;
            }
            OnPropertyChanged(nameof(OutputTestButtonText));
            OnPropertyChanged(nameof(OutputTestStatusBrush));
            NotifyCommandStates();
        }
    }
    public string OutputTestButtonText => IsOutputTestActive ? "Playing speaker test…" : "▶  Test Speakers";
    public Brush OutputTestStatusBrush => IsOutputTestActive
        ? Brushes.DeepSkyBlue
        : _outputTestSucceeded switch
        {
            true => Brushes.MediumSpringGreen,
            false => Brushes.OrangeRed,
            _ => Brushes.LightSlateGray,
        };
    public string OutputTestMessage
    {
        get => _outputTestMessage;
        private set => SetProperty(ref _outputTestMessage, value);
    }

    public bool IsEndSessionPanelOpen
    {
        get => _isEndSessionPanelOpen;
        set
        {
            if (SetProperty(ref _isEndSessionPanelOpen, value))
            {
                NotifyCommandStates();
            }
        }
    }
    public DestinationOptionViewModel? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (SetProperty(ref _selectedDestination, value))
            {
                NotifyCommandStates();
            }
        }
    }
    public bool IsBusy => _state.Mode == CoordinatorMode.Exporting;
    public bool IsBusyIndeterminate => _state.ExportProgress is null || _state.ExportProgress.FractionComplete <= 0;
    public double BusyProgress => Math.Clamp(_state.ExportProgress?.FractionComplete ?? 0, 0, 1);
    public string BusyTitle => "Saving your recordings";
    public string BusyMessage => _state.ExportProgress?.Message ?? "Preparing MP3 files…";
    public bool IsSuccessVisible => _state.Mode == CoordinatorMode.Completed;
    public string SuccessMessage
    {
        get
        {
            var result = _state.ExportResult;
            return result is null
                ? "Your recordings were saved successfully."
                : $"{result.ExportedFilePaths.Count} MP3 {(result.ExportedFilePaths.Count == 1 ? "file was" : "files were")} saved to\n{result.SessionFolderPath}";
        }
    }

    public bool IsInactivityWarningVisible
    {
        get => _isInactivityWarningVisible;
        private set
        {
            if (SetProperty(ref _isInactivityWarningVisible, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string InactivityCountdownText
    {
        get => _inactivityCountdownText;
        private set => SetProperty(ref _inactivityCountdownText, value);
    }

    public void NotifyUserActivity()
    {
        _lastUserActivityUtc = DateTimeOffset.UtcNow;
        _inactivityWarningStartedUtc = null;
        IsInactivityWarningVisible = false;
        if (_cursorHidden)
        {
            _cursorHidden = false;
            OnPropertyChanged(nameof(KioskCursor));
        }
    }

    public async Task InitializeAsync()
    {
        await ExecuteSafeAsync(() => _coordinator.InitializeAsync()).ConfigureAwait(true);
        if (_coordinator.State.Mode == CoordinatorMode.RecoveryAvailable)
        {
            var resume = _prompts.Confirm(
                "Interrupted session found",
                "A recording session was interrupted before it was exported. Resume it now? Selecting No moves it to administrator recovery without deleting it.",
                "Resume");
            await ExecuteSafeAsync(resume
                ? () => _coordinator.ResumeRecoveredSessionAsync()
                : () => _coordinator.AbandonRecoveredSessionAsync()).ConfigureAwait(true);
        }

        await Task.WhenAll(RefreshDevicesAsync(), RefreshFreeSpaceAsync(), RefreshExportPreflightAsync()).ConfigureAwait(true);
        _sessionTimer.Start();
        _recordingUiTimer.Start();
        NotifyUserActivity();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_state.Capabilities.CanSeek)
        {
            await ExecuteSafeAsync(() => _coordinator.SeekAsync(position)).ConfigureAwait(true);
        }
        else if (_state.Capabilities.CanPlay && position < SelectedDuration)
        {
            _displayPlayhead = position;
            OnPropertyChanged(nameof(Playhead));
        }
    }

    public async Task OpenConfigurationAsync()
    {
        if (!CanOpenConfiguration)
        {
            return;
        }

        _configurationEditorOpen = true;
        OnPropertyChanged(nameof(CanOpenConfiguration));
        NotifyUserActivity();
        try
        {
            await _configurationEditorLauncher.OpenAsync().ConfigureAwait(true);
            if (!await ExecuteSafeAsync(() => _coordinator.ReloadConfigurationAsync()).ConfigureAwait(true))
            {
                return;
            }

            // State notifications raised by the coordinator may be dispatcher-queued because
            // the reload performs file I/O off the UI thread. Apply the current snapshot now so
            // readiness checks use the newly saved format and destinations.
            ApplyState(_coordinator.State);
            var refreshed = await ExecuteSafeAsync(async () =>
                    await Task.WhenAll(RefreshDevicesAsync(), RefreshFreeSpaceAsync(), RefreshExportPreflightAsync()).ConfigureAwait(true),
                showDialog: true).ConfigureAwait(true);
            if (!refreshed)
            {
                return;
            }
            if (IsEndSessionPanelOpen)
            {
                BuildExportPreview();
            }
            StatusMessage = _state.Session is null
                ? "Settings applied. New sessions will use the updated configuration."
                : "Settings applied. Save locations are available now; audio format and device defaults apply to the next session.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            _prompts.ShowError("Settings", exception.Message);
        }
        finally
        {
            _configurationEditorOpen = false;
            OnPropertyChanged(nameof(CanOpenConfiguration));
            NotifyUserActivity();
        }
    }

    public Task CommitClipNameAsync()
    {
        if (SelectedClip is null || !_state.Capabilities.CanEditClips)
        {
            return Task.CompletedTask;
        }
        return ExecuteSafeAsync(() => _coordinator.RenameClipAsync(SelectedClip.Id, SelectedClip.Name));
    }

    public void HandleEscape()
    {
        if (IsDevicePanelOpen)
        {
            IsDevicePanelOpen = false;
        }
        else if (IsEndSessionPanelOpen && !IsBusy)
        {
            IsEndSessionPanelOpen = false;
        }
    }

    private async Task StartSessionAsync()
    {
        var name = SessionNameInput.Trim();
        if (name.Length == 0)
        {
            return;
        }
        await RefreshFreeSpaceAsync().ConfigureAwait(true);
        if (!_isReady)
        {
            _prompts.ShowError("Voice Booth is not ready", ReadinessMessage);
            return;
        }
        await ExecuteSafeAsync(() => _coordinator.StartSessionAsync(name)).ConfigureAwait(true);
        SessionNameInput = string.Empty;
    }

    private async Task RecordPauseAsync()
    {
        if (_state.Mode == CoordinatorMode.Recording)
        {
            await ExecuteSafeAsync(() => _coordinator.StopRecordingAsync()).ConfigureAwait(true);
        }
        else
        {
            await ExecuteSafeAsync(() => _coordinator.StartRecordingAsync()).ConfigureAwait(true);
        }
    }

    private async Task<bool> TryStopAsync()
    {
        if (_state.Mode == CoordinatorMode.Recording)
        {
            return await ExecuteSafeAsync(() => _coordinator.StopRecordingAsync()).ConfigureAwait(true);
        }
        if (_state.Mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
        {
            return await ExecuteSafeAsync(() => _coordinator.StopPlaybackAsync()).ConfigureAwait(true);
        }
        return true;
    }

    private async Task PlayPauseAsync()
    {
        if (_state.Mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
        {
            await ExecuteSafeAsync(() => _coordinator.StopPlaybackAsync()).ConfigureAwait(true);
        }
        else
        {
            var start = SelectedDuration > TimeSpan.Zero &&
                        _displayPlayhead >= SelectedDuration - TimeSpan.FromMilliseconds(50)
                ? TimeSpan.Zero
                : _displayPlayhead;
            _displayPlayhead = start;
            await ExecuteSafeAsync(() => _coordinator.PlaySelectedAsync(start)).ConfigureAwait(true);
        }
    }

    private async Task ReturnToStartAsync()
    {
        _displayPlayhead = TimeSpan.Zero;
        OnPropertyChanged(nameof(Playhead));
        if (_state.Capabilities.CanSeek)
        {
            await ExecuteSafeAsync(() => _coordinator.SeekAsync(TimeSpan.Zero)).ConfigureAwait(true);
        }
    }

    private async Task NewClipAsync()
    {
        var created = false;
        await ExecuteSafeAsync(async () => created = await _coordinator.NewClipAsync().ConfigureAwait(true)).ConfigureAwait(true);
        if (!created)
        {
            ClipNameFocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task NewTakeAsync()
    {
        if (SelectedClip is null)
        {
            return;
        }
        if (_state.Settings.SessionBehavior.ConfirmNewTake && !_prompts.Confirm(
                "Start a new take?",
                $"Reset “{SelectedClip.Name}” ({SelectedClip.DurationText}) to an empty take? You can undo until another recording begins."))
        {
            return;
        }
        await ExecuteSafeAsync(() => _coordinator.NewTakeAsync()).ConfigureAwait(true);
    }

    private async Task DeleteClipAsync(ClipItemViewModel? clip)
    {
        if (clip is null)
        {
            return;
        }
        if (_state.Settings.SessionBehavior.ConfirmDeleteClip && !_prompts.Confirm(
                "Delete clip?",
                $"Remove “{clip.Name}” ({clip.DurationText}) from this session? You can undo until another editing or recording action."))
        {
            return;
        }
        await ExecuteSafeAsync(() => _coordinator.DeleteClipAsync(clip.Id)).ConfigureAwait(true);
    }

    private async Task BeginRenameClipAsync(ClipItemViewModel? clip)
    {
        if (clip is null)
        {
            return;
        }
        if (_state.SelectedClip?.Id != clip.Id)
        {
            await ExecuteSafeAsync(() => _coordinator.SelectClipAsync(clip.Id)).ConfigureAwait(true);
        }
        ClipNameFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenDevicesAsync()
    {
        await RefreshDevicesAsync().ConfigureAwait(true);
        await ExecuteSafeAsync(LoadOutputVolumeAsync, showDialog: false).ConfigureAwait(true);
        IsDevicePanelOpen = true;
    }

    private void CloseDevices() => IsDevicePanelOpen = false;

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var inputsTask = _coordinator.GetDevicesAsync(AudioDeviceFlow.Input);
            var outputsTask = _coordinator.GetDevicesAsync(AudioDeviceFlow.Output);
            await Task.WhenAll(inputsTask, outputsTask).ConfigureAwait(true);

            _applyingDevices = true;
            try
            {
                PopulateDevices(InputDevices, inputsTask.Result, AudioDeviceFlow.Input);
                PopulateDevices(OutputDevices, outputsTask.Result, AudioDeviceFlow.Output);
                var inputSelection = _state.Session?.SettingsSnapshot.InputDevice ?? _state.Settings.Audio.InputDevice;
                var outputSelection = _state.Session?.SettingsSnapshot.OutputDevice ?? _state.Settings.Audio.OutputDevice;
                SelectedInputDevice = MatchSelection(InputDevices, inputSelection);
                SelectedOutputDevice = MatchSelection(OutputDevices, outputSelection);
            }
            finally
            {
                _applyingDevices = false;
            }

            _hasAvailableInput = SelectedInputDevice is { IsDefault: true }
                ? inputsTask.Result.Any(device => device.IsAvailable)
                : SelectedInputDevice?.IsAvailable == true;
            UpdateReadiness();
        }
        catch (Exception exception)
        {
            _isReady = false;
            _hasAvailableInput = false;
            ReadinessMessage = $"Audio check failed: {exception.Message}";
            OnPropertyChanged(nameof(ReadinessBrush));
            NotifyCommandStates();
        }
    }

    private bool CanToggleInputTest() => IsInputTestActive
        ? !_isInputTestStopping
        : SelectedInputDevice?.IsAvailable == true && _state.Capabilities.CanChangeDevices && !IsOutputTestActive;

    private Task ToggleInputTestAsync()
    {
        if (IsInputTestActive)
        {
            RequestStopInputTest("Stopping microphone test…");
            return Task.CompletedTask;
        }

        if (SelectedInputDevice is null)
        {
            return Task.CompletedTask;
        }

        var owner = new CancellationTokenSource();
        _inputTestCancellation = owner;
        _inputTestSucceeded = null;
        _isInputTestStopping = false;
        _displayInputLevel = 0;
        RaiseInputMeterProperties();
        InputTestMessage = "Listening — speak normally. The microphone stays open until you stop the test.";
        IsInputTestActive = true;

        var selection = ToSelection(SelectedInputDevice, AudioDeviceFlow.Input);
        var progress = new Progress<float>(level => ApplyInputTestLevel(owner, level));
        var operation = _deviceService.MonitorInputAsync(selection, progress, owner.Token);
        _inputTestTask = operation;
        var completion = CompleteInputTestAsync(operation, owner);
        if (!completion.IsCompleted)
        {
            _inputTestCompletionTask = completion;
        }
        _ = completion;
        return Task.CompletedTask;
    }

    private void ApplyInputTestLevel(CancellationTokenSource owner, float level)
    {
        if (!ReferenceEquals(_inputTestCancellation, owner) || owner.IsCancellationRequested || !IsInputTestActive)
        {
            return;
        }

        _displayInputLevel = Math.Clamp(level, 0, 1);
        RaiseInputMeterProperties();
    }

    private async Task CompleteInputTestAsync(Task<AudioDeviceTestResult> operation, CancellationTokenSource owner)
    {
        AudioDeviceTestResult result;
        try
        {
            result = await operation.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            result = new AudioDeviceTestResult(true, "Microphone test stopped.");
        }
        catch (Exception exception)
        {
            result = new AudioDeviceTestResult(false, $"The microphone could not be tested: {exception.Message}");
        }

        if (!ReferenceEquals(_inputTestCancellation, owner))
        {
            owner.Dispose();
            return;
        }

        _inputTestTask = null;
        _inputTestCompletionTask = null;
        _inputTestCancellation = null;
        _inputTestSucceeded = result.Succeeded;
        _isInputTestStopping = false;
        _displayInputLevel = 0;
        owner.Dispose();
        InputTestMessage = result.Message;
        IsInputTestActive = false;
        RaiseInputMeterProperties();
        OnPropertyChanged(nameof(InputTestButtonText));
        OnPropertyChanged(nameof(InputTestStatusBrush));
    }

    private void RequestStopInputTest(string message)
    {
        var cancellation = _inputTestCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        _isInputTestStopping = true;
        InputTestMessage = message;
        OnPropertyChanged(nameof(InputTestButtonText));
        NotifyCommandStates();
        cancellation.Cancel();
    }

    private async Task StopInputTestAndWaitAsync(string message)
    {
        var completion = _inputTestCompletionTask;
        RequestStopInputTest(message);
        if (completion is null)
        {
            return;
        }

        try
        {
            await completion.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // CompleteInputTestAsync owns user-facing failure reporting.
        }
    }

    private async Task TestOutputAsync()
    {
        if (SelectedOutputDevice is null)
        {
            return;
        }

        await StopInputTestAndWaitAsync("Stopping microphone test before the speaker test…").ConfigureAwait(true);
        var owner = new CancellationTokenSource();
        _outputTestCancellation = owner;
        _outputTestSucceeded = null;
        IsOutputTestActive = true;
        OutputTestMessage = $"Playing a two-note test through {SelectedOutputDevice.DisplayName}…";

        AudioDeviceTestResult result;
        try
        {
            var operation = _deviceService.TestOutputAsync(
                ToSelection(SelectedOutputDevice, AudioDeviceFlow.Output),
                owner.Token);
            _outputTestTask = operation;
            result = await operation.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            result = new AudioDeviceTestResult(true, "Speaker test stopped.");
        }
        catch (Exception exception)
        {
            result = new AudioDeviceTestResult(false, $"The speakers could not be tested: {exception.Message}");
        }

        if (!ReferenceEquals(_outputTestCancellation, owner))
        {
            owner.Dispose();
            return;
        }

        _outputTestTask = null;
        _outputTestCancellation = null;
        _outputTestSucceeded = result.Succeeded;
        owner.Dispose();
        OutputTestMessage = result.Message;
        IsOutputTestActive = false;
        OnPropertyChanged(nameof(OutputTestStatusBrush));
    }

    private void RequestStopOutputTest()
    {
        var cancellation = _outputTestCancellation;
        if (cancellation is not null && !cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }
    }

    private void RaiseInputMeterProperties()
    {
        OnPropertyChanged(nameof(InputLevel));
        OnPropertyChanged(nameof(InputLevelText));
        OnPropertyChanged(nameof(InputLevelBrush));
        OnPropertyChanged(nameof(InputLevelStatus));
    }

    private async Task LoadOutputVolumeAsync()
    {
        if (!_state.Settings.Audio.AllowOutputVolumeControl || SelectedOutputDevice?.IsAvailable != true)
        {
            return;
        }

        var volume = await _deviceService.GetOutputVolumeAsync(
            ToSelection(SelectedOutputDevice, AudioDeviceFlow.Output)).ConfigureAwait(true);
        if (volume is null)
        {
            return;
        }

        _applyingOutputVolume = true;
        try
        {
            OutputVolume = volume.Value;
            OnPropertyChanged(nameof(OutputVolumeText));
        }
        finally
        {
            _applyingOutputVolume = false;
        }
    }

    private async Task DebounceOutputVolumeAsync()
    {
        _volumeChangeCancellation?.Cancel();
        _volumeChangeCancellation?.Dispose();
        _volumeChangeCancellation = new CancellationTokenSource();
        var cancellationToken = _volumeChangeCancellation.Token;
        try
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(true);
            if (SelectedOutputDevice?.IsAvailable == true)
            {
                await _deviceService.SetOutputVolumeAsync(
                    ToSelection(SelectedOutputDevice, AudioDeviceFlow.Output),
                    (float)OutputVolume,
                    cancellationToken).ConfigureAwait(true);
                OnPropertyChanged(nameof(OutputVolumeText));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            OutputTestMessage = $"Volume could not be changed: {exception.Message}";
        }
    }

    private async Task OpenEndSessionAsync()
    {
        if (_state.Mode == CoordinatorMode.Recording || _state.Mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
        {
            if (!await TryStopAsync().ConfigureAwait(true))
            {
                return;
            }
        }

        if (_state.Session?.HasUnresolvedSegments == true)
        {
            _prompts.ShowError(
                "Recording needs recovery",
                "A recording segment could not be safely finalized. The session has been kept locally; start a new take or have an administrator recover it before exporting.");
            return;
        }

        ApplyState(_coordinator.State);
        if (HasRecordedAudio)
        {
            BuildExportPreview();
        }
        else
        {
            ExportFilePreviews.Clear();
        }
        IsEndSessionPanelOpen = true;
    }

    private async Task ExportAndEndAsync()
    {
        if (!HasRecordedAudio)
        {
            IsEndSessionPanelOpen = false;
            if (await ExecuteSafeAsync(() => _coordinator.EndEmptySessionAsync()).ConfigureAwait(true))
            {
                SetWaveformPeaks([]);
                _loadedWaveformClipId = null;
                _displayPlayhead = TimeSpan.Zero;
                NotifyUserActivity();
            }
            return;
        }

        if (SelectedDestination is null)
        {
            return;
        }
        IsEndSessionPanelOpen = false;
        await ExecuteSafeAsync(() => _coordinator.EndSessionAsync(SelectedDestination.Id)).ConfigureAwait(true);
    }

    private async Task FinishSuccessAsync()
    {
        if (_successResetInProgress)
        {
            return;
        }

        _successResetInProgress = true;
        try
        {
            await ExecuteSafeAsync(() => _coordinator.ResetAfterCompletedSessionAsync()).ConfigureAwait(true);
            SetWaveformPeaks([]);
            _loadedWaveformClipId = null;
            _displayPlayhead = TimeSpan.Zero;
            _successShownAtUtc = null;
            NotifyUserActivity();
        }
        finally
        {
            _successResetInProgress = false;
        }
    }

    private async Task RefreshWaveformAsync()
    {
        var clip = _state.SelectedClip;
        if (clip?.HasAudio != true || _state.Mode == CoordinatorMode.Recording)
        {
            if (clip?.HasAudio != true)
            {
                SetWaveformPeaks([]);
                _loadedWaveformClipId = clip?.Id;
            }
            return;
        }

        _waveformLoadCancellation?.Cancel();
        _waveformLoadCancellation?.Dispose();
        _waveformLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _waveformLoadCancellation.Token;
        try
        {
            var waveform = await _coordinator.LoadSelectedWaveformAsync(1600, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || _state.SelectedClip?.Id != clip.Id)
            {
                return;
            }
            SetWaveformPeaks(waveform.Peaks.Select(static peak =>
                (double)Math.Max(Math.Abs(peak.Minimum), Math.Abs(peak.Maximum))));
            _loadedWaveformClipId = clip.Id;
            _displayPlayhead = TimeSpan.Zero;
            OnPropertyChanged(nameof(Playhead));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshFreeSpaceAsync()
    {
        try
        {
            var bytes = await _workingStorage.GetAvailableBytesAsync().ConfigureAwait(true);
            FreeSpaceText = $"{FormatBytes(bytes)} free locally";
            _hasAdequateLocalStorage = bytes >= MinimumLocalFreeBytes;
            UpdateReadiness();
        }
        catch
        {
            FreeSpaceText = "Local storage status unavailable";
            _hasAdequateLocalStorage = false;
            UpdateReadiness();
        }
    }

    private async Task RefreshExportPreflightAsync()
    {
        try
        {
            if (_exportService is not IExportPreflightService preflight)
            {
                _hasAvailableEncoder = true;
                _exportPreflightMessage = null;
                UpdateReadiness();
                return;
            }

            var format = _state.Session?.SettingsSnapshot.RecordingFormat ?? _state.Settings.Audio.RecordingFormat;
            var bitrate = _state.Session?.SettingsSnapshot.Mp3BitrateKbps ?? _state.Settings.Audio.Mp3BitrateKbps;
            var encoder = await preflight.ValidateEncoderAsync(format, bitrate).ConfigureAwait(true);
            _hasAvailableEncoder = encoder.IsValid;
            _exportPreflightMessage = encoder.Message;

            if (_state.Settings.Diagnostics.RunHardwareSelfTestOnStartup)
            {
                var destination = _state.Settings.Destinations.FirstOrDefault(candidate =>
                    candidate.IsEnabled && string.Equals(candidate.Id, _state.Settings.DefaultDestinationId, StringComparison.Ordinal))
                    ?? _state.Settings.Destinations.FirstOrDefault(candidate => candidate.IsEnabled);
                if (destination is not null)
                {
                    var destinationResult = await _exportService.ValidateDestinationAsync(destination).ConfigureAwait(true);
                    if (!destinationResult.IsValid)
                    {
                        StatusMessage = $"Startup check: {destinationResult.Message} Recording remains local until a destination is available.";
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _hasAvailableEncoder = false;
            _exportPreflightMessage = $"MP3 export check failed: {exception.Message}";
        }
        finally
        {
            UpdateReadiness();
        }
    }

    private void BuildExportPreview()
    {
        Destinations.Clear();
        foreach (var destination in _state.Settings.Destinations.Where(item => item.IsEnabled))
        {
            Destinations.Add(new(destination.Id, destination.Label, destination.RootPath));
        }
        SelectedDestination = Destinations.FirstOrDefault(item => item.Id == _state.Settings.DefaultDestinationId)
            ?? Destinations.FirstOrDefault();

        ExportFilePreviews.Clear();
        if (_state.Session is not null)
        {
            var plan = OutputNamePlanner.Create(_state.Session);
            foreach (var file in plan.ClipFiles)
            {
                ExportFilePreviews.Add(file.FileName);
            }
        }
        OnPropertyChanged(nameof(ExportClipCountText));
        OnPropertyChanged(nameof(SessionRecordedDurationText));
    }

    private void OnCoordinatorStateChanged(object? sender, SessionCoordinatorStateChangedEventArgs eventArgs)
    {
        if (eventArgs.State.Mode == CoordinatorMode.Recording)
        {
            lock (_pendingRecordingGate)
            {
                _pendingRecordingState = eventArgs.State;
                _pendingRecordingPeaks.AddRange(eventArgs.State.LiveWaveformPeaks);
                if (_pendingRecordingPeaks.Count > MaximumWaveformPeaks)
                {
                    _pendingRecordingPeaks.RemoveRange(0, _pendingRecordingPeaks.Count - MaximumWaveformPeaks);
                }
            }
            return;
        }

        void ApplyNonRecordingState()
        {
            FlushPendingRecordingState();
            ApplyState(eventArgs.State);
        }

        if (_dispatcher.CheckAccess())
        {
            ApplyNonRecordingState();
        }
        else
        {
            _dispatcher.BeginInvoke(ApplyNonRecordingState);
        }
    }

    private void OnRecordingUiTimerTick(object? sender, EventArgs eventArgs) => FlushPendingRecordingState();

    private void FlushPendingRecordingState()
    {
        SessionCoordinatorState? state;
        WaveformPeak[] peaks;
        lock (_pendingRecordingGate)
        {
            state = _pendingRecordingState;
            if (state is null)
            {
                return;
            }
            peaks = _pendingRecordingPeaks.ToArray();
            _pendingRecordingPeaks.Clear();
            _pendingRecordingState = null;
        }

        ApplyState(state with { LiveWaveformPeaks = peaks });
    }

    private void OnCoordinatorOperationFailed(object? sender, ApplicationOperationFailedEventArgs eventArgs)
    {
        void Update() => StatusMessage = eventArgs.UserMessage;
        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    private void OnDevicesChanged(object? sender, AudioDevicesChangedEventArgs eventArgs)
    {
        void Refresh()
        {
            RequestStopInputTest("Audio devices changed. Select Start Mic Test to test the new selection.");
            RequestStopOutputTest();
            _ = ExecuteSafeAsync(RefreshDevicesAsync, showDialog: false);
        }
        if (_dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            _dispatcher.BeginInvoke(Refresh);
        }
    }

    private void ApplyState(SessionCoordinatorState state)
    {
        if (state.Version <= _lastAppliedStateVersion)
        {
            return;
        }

        var previousMode = _state.Mode;
        var previousClipId = _state.SelectedClip?.Id;
        _state = state;
        _lastAppliedStateVersion = state.Version;
        StatusMessage = state.ErrorMessage ?? state.StatusMessage ?? "Ready.";

        if (state.Settings.Accessibility.AudibleStatusCues && previousMode != state.Mode)
        {
            TryPlayStatusCue(state.Mode);
        }

        if (!ReferenceEquals(_appliedVisualSettings, state.Settings))
        {
            ApplyVisualSettings(state.Settings);
            _appliedVisualSettings = state.Settings;
        }

        if (state.Mode == CoordinatorMode.Completed && previousMode != CoordinatorMode.Completed)
        {
            _successShownAtUtc = DateTimeOffset.UtcNow;
        }
        else if (state.Mode != CoordinatorMode.Completed)
        {
            _successShownAtUtc = null;
        }

        if (state.Session is null)
        {
            IsInactivityWarningVisible = false;
            _inactivityWarningStartedUtc = null;
        }

        _applyingState = true;
        try
        {
            SynchronizeClips(state);
            SelectedClip = state.SelectedClip is null ? null : Clips.FirstOrDefault(clip => clip.Id == state.SelectedClip.Id);
        }
        finally
        {
            _applyingState = false;
        }

        if (state.Mode == CoordinatorMode.Recording)
        {
            if (previousMode != CoordinatorMode.Recording && state.SelectedClip?.HasAudio != true)
            {
                SetWaveformPeaks([]);
            }
            AppendWaveformPeaks(state.LiveWaveformPeaks);
        }
        else if (state.SelectedClip?.HasAudio == true &&
                 (previousClipId != state.SelectedClip.Id || previousMode == CoordinatorMode.Recording || _loadedWaveformClipId != state.SelectedClip.Id))
        {
            _ = ExecuteSafeAsync(RefreshWaveformAsync, showDialog: false);
        }
        else if (state.SelectedClip?.HasAudio != true)
        {
            SetWaveformPeaks([]);
            _loadedWaveformClipId = state.SelectedClip?.Id;
        }

        if (state.Mode is CoordinatorMode.Playing or CoordinatorMode.Paused)
        {
            _displayPlayhead = state.PlaybackPosition;
        }
        else if (previousClipId != state.SelectedClip?.Id)
        {
            _displayPlayhead = TimeSpan.Zero;
        }

        RaiseAllStateProperties();
        NotifyCommandStates();
    }

    private void SynchronizeClips(SessionCoordinatorState state)
    {
        var source = state.Session?.ActiveClips.OrderBy(clip => clip.Ordinal).ToArray() ?? [];
        var sourceIds = source.Select(clip => clip.Id).ToHashSet();
        for (var index = Clips.Count - 1; index >= 0; index--)
        {
            if (!sourceIds.Contains(Clips[index].Id))
            {
                Clips.RemoveAt(index);
            }
        }

        foreach (var clip in source)
        {
            var item = Clips.FirstOrDefault(existing => existing.Id == clip.Id);
            if (item is null)
            {
                item = new ClipItemViewModel { Id = clip.Id, Ordinal = clip.Ordinal, Name = clip.DisplayName };
                Clips.Add(item);
            }
            item.Name = clip.DisplayName;
            item.Duration = clip.Duration + (state.Mode == CoordinatorMode.Recording && state.SelectedClip?.Id == clip.Id ? state.RecordingPosition : TimeSpan.Zero);
            item.IsEmpty = !clip.HasAudio && clip.State != ClipState.Recording;
            item.Status = clip.State switch
            {
                ClipState.Empty => "Empty",
                ClipState.Recording => "Recording",
                ClipState.Playing => "Playing",
                ClipState.Paused => "Paused",
                ClipState.Error => "Needs attention",
                _ => "Recorded"
            };
        }

        // ObservableCollection preserves insertion order, so explicitly restore the
        // domain ordinal after undo/recovery operations that reintroduce an older clip.
        for (var targetIndex = 0; targetIndex < source.Length; targetIndex++)
        {
            var currentIndex = Clips.IndexOf(Clips.First(item => item.Id == source[targetIndex].Id));
            if (currentIndex != targetIndex)
            {
                Clips.Move(currentIndex, targetIndex);
            }
        }
        OnPropertyChanged(nameof(ClipCountText));
    }

    private void RaiseAllStateProperties()
    {
        foreach (var property in new[]
        {
            nameof(ApplicationTitle), nameof(ApplicationSubtitle), nameof(WelcomeInstructions), nameof(SessionNameHelp), nameof(RecordingInstructions),
            nameof(PrivacyNotice), nameof(DeviceInstructions), nameof(EndSessionInstructions), nameof(EndSessionPanelInstructions),
            nameof(EndSessionPrimaryButtonText), nameof(WelcomeExtraContent),
            nameof(RecordingExtraContent), nameof(DeviceExtraContent), nameof(EndSessionExtraContent), nameof(LogoSource), nameof(HasLogo),
            nameof(AllowDeviceSelection), nameof(AllowOutputVolumeControl), nameof(ShowHoldToExit), nameof(FullScreen), nameof(AlwaysOnTop), nameof(CanOpenConfiguration), nameof(KioskCursor),
            nameof(TextScale), nameof(ControlButtonMinHeight), nameof(ClipRailColumn), nameof(MainWorkspaceColumn),
            nameof(LeftWorkspaceWidth), nameof(RightWorkspaceWidth), nameof(WaveformBrush), nameof(WaveformBackgroundBrush), nameof(RecordingWaveformBrush), nameof(IsSessionActive),
            nameof(CurrentSessionName), nameof(SessionElapsedText), nameof(CanEditClipName), nameof(CanUndo), nameof(SelectedDuration),
            nameof(SelectedDurationText), nameof(SessionRecordedDurationText), nameof(ExportClipCountText), nameof(HasRecordedAudio), nameof(IsRecording),
            nameof(IsPlaying), nameof(Playhead), nameof(InputLevel), nameof(InputLevelText), nameof(InputLevelBrush), nameof(InputLevelStatus), nameof(RecordButtonText),
            nameof(IsInputTestActive), nameof(InputTestButtonText), nameof(InputTestStatusBrush), nameof(InputTestMessage),
            nameof(IsOutputTestActive), nameof(OutputTestButtonText), nameof(OutputTestStatusBrush), nameof(OutputTestMessage),
            nameof(PlayButtonText), nameof(TransportStatus), nameof(StatusIndicatorBrush), nameof(ContextMessage),
            nameof(AutosaveMessage), nameof(AutosaveBrush), nameof(IsBusy), nameof(IsBusyIndeterminate),
            nameof(BusyProgress), nameof(BusyTitle), nameof(BusyMessage), nameof(IsSuccessVisible), nameof(SuccessMessage)
        })
        {
            OnPropertyChanged(property);
        }
    }

    private bool CanStartSession() => _state.Capabilities.CanStartSession && _isReady && !string.IsNullOrWhiteSpace(SessionNameInput);

    private static bool HasCommandLineSwitch(string value) =>
        Environment.GetCommandLineArgs().Contains(value, StringComparer.OrdinalIgnoreCase);
    private bool CanRecordPause() => _state.Mode == CoordinatorMode.Recording
        ? _state.Capabilities.CanStop
        : _state.Capabilities.CanRecord && _hasAvailableInput && _hasAdequateLocalStorage && _hasAvailableEncoder;
    private bool CanPlayPause() => _state.Capabilities.CanPlay || _state.Capabilities.CanPause || _state.Capabilities.CanResume;
    private bool CanOpenEndSession() => _state.Capabilities.CanEndSession;

    private void NotifyCommandStates()
    {
        foreach (var command in new[]
        {
            StartSessionCommand, RecordCommand, PlayPauseCommand, ReturnToStartCommand, NewClipCommand,
            NewTakeCommand, UndoCommand, DeleteClipCommand, BeginRenameClipCommand, OpenDevicesCommand,
            TestInputCommand, TestOutputCommand, OpenEndSessionCommand, ExportAndEndCommand, FinishSuccessCommand,
            ContinueSessionCommand, CloseEndSessionCommand
        })
        {
            switch (command)
            {
                case AsyncRelayCommand asyncCommand:
                    asyncCommand.NotifyCanExecuteChanged();
                    break;
                case AsyncRelayCommand<ClipItemViewModel> clipCommand:
                    clipCommand.NotifyCanExecuteChanged();
                    break;
                case RelayCommand relayCommand:
                    relayCommand.NotifyCanExecuteChanged();
                    break;
            }
        }
    }

    private async Task<bool> ExecuteSafeAsync(Func<Task> action, bool showDialog = true)
    {
        try
        {
            await action().ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation canceled. Your local recording is safe.";
            return false;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            if (showDialog)
            {
                _prompts.ShowError("Voice Booth", exception.Message);
            }
            return false;
        }
    }

    private void OnSessionTimerTick(object? sender, EventArgs eventArgs)
    {
        var now = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(SessionElapsedText));
        if (IsSessionActive && now.Second % 30 == 0)
        {
            _ = RefreshFreeSpaceAsync();
        }

        if (_state.Settings.Kiosk.HideCursorWhenIdle && !_cursorHidden && now - _lastUserActivityUtc >= TimeSpan.FromSeconds(5))
        {
            _cursorHidden = true;
            OnPropertyChanged(nameof(KioskCursor));
        }

        UpdateInactivityState(now);

        var successSeconds = _state.Settings.SessionBehavior.SuccessScreenSeconds;
        if (_state.Mode == CoordinatorMode.Completed && successSeconds > 0 && _successShownAtUtc is not null &&
            now - _successShownAtUtc.Value >= TimeSpan.FromSeconds(successSeconds))
        {
            _ = FinishSuccessAsync();
        }
    }

    private void UpdateInactivityState(DateTimeOffset now)
    {
        if (_configurationEditorOpen)
        {
            _inactivityWarningStartedUtc = null;
            IsInactivityWarningVisible = false;
            return;
        }

        var warningMinutes = _state.Settings.SessionBehavior.InactivityWarningMinutes;
        if (_state.Mode is not (CoordinatorMode.Ready or CoordinatorMode.Playing or CoordinatorMode.Paused) || warningMinutes <= 0)
        {
            _inactivityWarningStartedUtc = null;
            IsInactivityWarningVisible = false;
            return;
        }

        if (_inactivityWarningStartedUtc is null)
        {
            if (now - _lastUserActivityUtc < TimeSpan.FromMinutes(warningMinutes))
            {
                return;
            }
            _inactivityWarningStartedUtc = now;
            IsInactivityWarningVisible = true;
        }

        var remaining = InactivityCountdown - (now - _inactivityWarningStartedUtc.Value);
        if (remaining > TimeSpan.Zero)
        {
            InactivityCountdownText = $"This session will be secured in {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} seconds.";
            return;
        }

        if (!_inactivityResetInProgress)
        {
            _ = SecureInactiveSessionAsync();
        }
    }

    private async Task SecureInactiveSessionAsync()
    {
        _inactivityResetInProgress = true;
        try
        {
            IsInactivityWarningVisible = false;
            await ExecuteSafeAsync(() => _coordinator.QuarantineActiveSessionAsync()).ConfigureAwait(true);
            SetWaveformPeaks([]);
            _loadedWaveformClipId = null;
            _displayPlayhead = TimeSpan.Zero;
            NotifyUserActivity();
        }
        finally
        {
            _inactivityResetInProgress = false;
        }
    }

    private string Content(string id, string fallback) =>
        _state.Settings.ContentBlocks.FirstOrDefault(block => block.IsVisible && string.Equals(block.Id, id, StringComparison.OrdinalIgnoreCase))?.Text
        ?? fallback;

    private IReadOnlyList<ContentBlock> ExtraContent(string region) =>
        _state.Settings.ContentBlocks
            .Where(block => block.IsVisible &&
                (block.Id.StartsWith($"{region}.extra.", StringComparison.OrdinalIgnoreCase) ||
                 block.Id.StartsWith($"{region}.custom", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private void UpdateReadiness()
    {
        _isReady = _hasAvailableInput && _hasAdequateLocalStorage && _hasAvailableEncoder;
        ReadinessMessage = !_hasAvailableInput
            ? "No available microphone. Open Audio Devices to reconnect."
            : !_hasAdequateLocalStorage
                ? "Local recording storage is low or unavailable. Ask an administrator for help."
                : !_hasAvailableEncoder
                    ? _exportPreflightMessage ?? "The configured MP3 encoder is unavailable."
                    : $"Microphone and MP3 export ready: {SelectedInputDevice?.DisplayName ?? "Windows default input"}";
        OnPropertyChanged(nameof(ReadinessBrush));
        NotifyCommandStates();
    }

    private void SetWaveformPeaks(IEnumerable<double> values)
    {
        var materialized = values.Select(static value => Math.Clamp(Math.Abs(value), 0, 1)).ToArray();
        var bounded = materialized.Length <= MaximumWaveformPeaks
            ? materialized
            : DownsampleMaximums(materialized, MaximumWaveformPeaks);
        SetProperty(ref _waveformPeaks, bounded, nameof(WaveformPeaks));
    }

    private void AppendWaveformPeaks(IReadOnlyList<WaveformPeak> peaks)
    {
        if (peaks.Count == 0)
        {
            return;
        }

        var combined = new double[_waveformPeaks.Count + peaks.Count];
        for (var index = 0; index < _waveformPeaks.Count; index++)
        {
            combined[index] = _waveformPeaks[index];
        }
        for (var index = 0; index < peaks.Count; index++)
        {
            combined[_waveformPeaks.Count + index] = Math.Max(Math.Abs(peaks[index].Minimum), Math.Abs(peaks[index].Maximum));
        }

        SetWaveformPeaks(combined.Length > MaximumWaveformPeaks
            ? DownsampleMaximums(combined, WaveformCompactionTarget)
            : combined);
    }

    private static double[] DownsampleMaximums(IReadOnlyList<double> source, int targetCount)
    {
        if (source.Count <= targetCount)
        {
            return source.ToArray();
        }

        var result = new double[targetCount];
        for (var target = 0; target < targetCount; target++)
        {
            var start = (int)((long)target * source.Count / targetCount);
            var end = Math.Max(start + 1, (int)((long)(target + 1) * source.Count / targetCount));
            var maximum = 0d;
            for (var sourceIndex = start; sourceIndex < Math.Min(end, source.Count); sourceIndex++)
            {
                maximum = Math.Max(maximum, Math.Abs(source[sourceIndex]));
            }
            result[target] = maximum;
        }
        return result;
    }

    private void ApplyVisualSettings(KioskSettings settings)
    {
        ApplyApplicationTheme(settings);
        _logoSource = LoadLogo(settings.Branding.LogoPath);
        OnPropertyChanged(nameof(LogoSource));
        OnPropertyChanged(nameof(HasLogo));
    }

    public static void ApplyApplicationTheme(KioskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var highContrast = settings.Accessibility.HighContrast;
        var canvas = highContrast
            ? Colors.Black
            : ParseColor(settings.Branding.BackgroundColor, Color.FromRgb(8, 17, 31));
        var primary = ParseColor(settings.Branding.PrimaryColor, Color.FromRgb(76, 201, 240));
        var record = ParseColor(settings.Branding.AccentColor, Color.FromRgb(240, 68, 90));
        var lightCanvas = RelativeLuminance(canvas) > 0.5;
        var surface = highContrast
            ? Color.FromRgb(12, 12, 12)
            : Blend(canvas, lightCanvas ? Colors.Black : Colors.White, lightCanvas ? 0.06 : 0.07);
        var raised = highContrast
            ? Color.FromRgb(28, 28, 28)
            : Blend(canvas, lightCanvas ? Colors.Black : Colors.White, lightCanvas ? 0.12 : 0.12);
        var border = highContrast
            ? Colors.White
            : Blend(canvas, lightCanvas ? Colors.Black : Colors.White, 0.22);
        var text = lightCanvas && !highContrast ? Color.FromRgb(15, 29, 48) : Colors.White;
        var muted = lightCanvas && !highContrast
            ? Color.FromRgb(65, 82, 103)
            : highContrast ? Color.FromRgb(235, 235, 235) : Color.FromRgb(158, 176, 196);

        SetThemeColor("CanvasColor", canvas);
        SetThemeBrush("CanvasBrush", canvas);
        SetThemeBrush("SurfaceBrush", surface);
        SetThemeBrush("SurfaceRaisedBrush", raised);
        SetThemeBrush("BorderBrush", border);
        SetThemeBrush("TextBrush", text);
        SetThemeBrush("MutedTextBrush", muted);
        SetThemeBrush("AccentBrush", primary);
        SetThemeBrush("AccentDarkBrush", Blend(primary, Colors.Black, 0.35));
        SetThemeBrush("RecordBrush", record);
        if (System.Windows.Application.Current is { } application)
        {
            application.Resources["PressedScale"] = settings.Accessibility.ReducedMotion ? 1d : 0.98d;
        }

    }

    private static ImageSource? LoadLogo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static Brush ParseBrush(string? value, Color fallback)
    {
        var brush = new SolidColorBrush(ParseColor(value, fallback));
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch
        {
            return fallback;
        }
    }

    private static void SetThemeColor(string key, Color color)
    {
        if (System.Windows.Application.Current is { } application)
        {
            application.Resources[key] = color;
        }
    }

    private static void SetThemeBrush(string key, Color color)
    {
        if (System.Windows.Application.Current is not { } application)
        {
            return;
        }

        if (application.Resources[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
        }
        else
        {
            var replacement = new SolidColorBrush(color);
            replacement.Freeze();
            application.Resources[key] = replacement;
        }
    }

    private static Color Blend(Color source, Color target, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(source.R + (target.R - source.R) * amount),
            (byte)Math.Round(source.G + (target.G - source.G) * amount),
            (byte)Math.Round(source.B + (target.B - source.B) * amount));
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;

    private static void TryPlayStatusCue(CoordinatorMode mode)
    {
        try
        {
            if (mode == CoordinatorMode.Recording)
            {
                SystemSounds.Beep.Play();
            }
            else if (mode == CoordinatorMode.Completed)
            {
                SystemSounds.Asterisk.Play();
            }
        }
        catch
        {
            // Accessibility cues are advisory and never affect recording state.
        }
    }

    private static void PopulateDevices(ObservableCollection<DeviceOptionViewModel> target, IReadOnlyList<AudioDeviceInfo> devices, AudioDeviceFlow flow)
    {
        target.Clear();
        target.Add(new(WindowsDefaultDeviceId, $"Windows default {flow.ToString().ToLowerInvariant()}", true));
        foreach (var device in devices.Where(device => device.IsAvailable).OrderByDescending(device => device.IsDefault).ThenBy(device => device.FriendlyName))
        {
            target.Add(new(device.EndpointId, device.FriendlyName, false));
        }
    }

    private static DeviceOptionViewModel? MatchSelection(
        ObservableCollection<DeviceOptionViewModel> devices,
        AudioDeviceSelection selection)
    {
        if (selection.UseDefault)
        {
            return devices.FirstOrDefault(device => device.Id == WindowsDefaultDeviceId);
        }

        var match = devices.FirstOrDefault(device => string.Equals(device.Id, selection.EndpointId, StringComparison.Ordinal));
        if (match is not null)
        {
            return match;
        }

        if (!string.IsNullOrWhiteSpace(selection.EndpointId))
        {
            var unavailable = new DeviceOptionViewModel(
                selection.EndpointId,
                string.IsNullOrWhiteSpace(selection.FriendlyNameFallback) ? "Saved device" : selection.FriendlyNameFallback,
                IsDefault: false,
                IsAvailable: false);
            devices.Add(unavailable);
            return unavailable;
        }

        return devices.FirstOrDefault();
    }

    private static AudioDeviceSelection ToSelection(DeviceOptionViewModel device, AudioDeviceFlow flow) => new()
    {
        Flow = flow,
        UseDefault = device.Id == WindowsDefaultDeviceId,
        EndpointId = device.Id == WindowsDefaultDeviceId ? null : device.Id,
        FriendlyNameFallback = device.Name
    };

    private static string FormatDuration(TimeSpan value) =>
        value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sessionTimer.Stop();
        _recordingUiTimer.Stop();
        var inputTestTask = _inputTestTask;
        var outputTestTask = _outputTestTask;
        RequestStopInputTest("Microphone test stopped.");
        RequestStopOutputTest();
        await IgnoreDeviceTestCompletionAsync(inputTestTask).ConfigureAwait(false);
        await IgnoreDeviceTestCompletionAsync(outputTestTask).ConfigureAwait(false);
        _waveformLoadCancellation?.Cancel();
        _waveformLoadCancellation?.Dispose();
        _volumeChangeCancellation?.Cancel();
        _volumeChangeCancellation?.Dispose();
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.OperationFailed -= OnCoordinatorOperationFailed;
        _deviceService.DevicesChanged -= OnDevicesChanged;
        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task IgnoreDeviceTestCompletionAsync(Task<AudioDeviceTestResult>? operation)
    {
        if (operation is null)
        {
            return;
        }

        try
        {
            _ = await operation.ConfigureAwait(false);
        }
        catch
        {
            // Application shutdown still continues after a best-effort device-test stop.
        }
    }
}
