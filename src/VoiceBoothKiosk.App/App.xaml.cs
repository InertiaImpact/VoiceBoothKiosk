using System.Windows;
using System.Windows.Threading;
using System.IO;
using VoiceBoothKiosk.App.Services;
using VoiceBoothKiosk.App.ViewModels;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Audio.Windows;
using VoiceBoothKiosk.Infrastructure.Configuration;
using VoiceBoothKiosk.Infrastructure.Logging;
using VoiceBoothKiosk.Infrastructure.Persistence;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.App;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = "Local\\VoiceBoothKiosk.Recorder.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private RollingFileLogger? _logger;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, InstanceMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("Voice Booth is already running.", "Voice Booth", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var paths = new ApplicationPaths(
                EmptyToNull(Environment.GetEnvironmentVariable("VOICEBOOTH_MACHINE_DATA_ROOT")),
                EmptyToNull(Environment.GetEnvironmentVariable("VOICEBOOTH_LOCAL_DATA_ROOT")));
            paths.EnsureLocalDirectories();
            _logger = CreateLogger(paths);
            _logger.Write(AppLogLevel.Information, 1000, "Voice Booth starting.", properties: new Dictionary<string, object?>
            {
                ["version"] = typeof(App).Assembly.GetName().Version?.ToString(),
                ["os"] = Environment.OSVersion.VersionString,
                ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            });

            var json = new AtomicJsonFile();
            var machineConfiguration = new JsonConfigurationService(paths, json);
            var initialSettings = machineConfiguration.LoadAsync().GetAwaiter().GetResult();
            ApplyLoggingPolicy(_logger, initialSettings);
            MainViewModel.ApplyApplicationTheme(initialSettings);
            machineConfiguration.SettingsChanged += (_, args) => ApplyLoggingPolicy(_logger, args.Settings);
            IConfigurationService configuration = new LayeredKioskConfigurationService(machineConfiguration, json);
            var sessionRepository = new JsonSessionRepository(paths, json);
            var workingStorage = new FileWorkingStorage(paths, json);

            var devices = new WasapiAudioDeviceService();
            var capture = new WasapiAudioCaptureService(devices);
            var playback = new WasapiAudioPlaybackService(devices);
            var waveforms = new WaveformCacheService();
            var export = new MediaFoundationExportService();

            var coordinator = new SessionCoordinator(
                capture,
                playback,
                devices,
                waveforms,
                export,
                sessionRepository,
                workingStorage,
                configuration,
                new SystemClock());

            var loggingSettings = initialSettings;
            coordinator.StateChanged += (_, args) =>
            {
                if (ReferenceEquals(loggingSettings, args.State.Settings))
                {
                    return;
                }
                loggingSettings = args.State.Settings;
                ApplyLoggingPolicy(_logger, loggingSettings);
            };

            _mainViewModel = new MainViewModel(
                coordinator,
                devices,
                workingStorage,
                export,
                new UserPromptService(),
                new ConfigurationEditorLauncher());
            var mainWindow = new MainWindow { DataContext = _mainViewModel };
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            _logger?.Write(AppLogLevel.Critical, 1001, "Voice Booth failed during startup.", exception);
            MessageBox.Show(
                $"Voice Booth could not start.\n\n{exception.Message}\n\nNo recording was discarded.",
                "Voice Booth startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _mainViewModel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _logger?.Write(AppLogLevel.Information, 1002, "Voice Booth stopped.", properties: new Dictionary<string, object?>
            {
                ["exitCode"] = e.ApplicationExitCode
            });
        }
        catch (Exception exception)
        {
            _logger?.Write(AppLogLevel.Error, 1003, "Cleanup failed during application exit.", exception);
        }
        finally
        {
            _logger?.Dispose();
            if (_singleInstanceMutex is not null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
                _singleInstanceMutex.Dispose();
            }
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Write(AppLogLevel.Error, 1100, "Unhandled UI exception.", e.Exception);
        MessageBox.Show(
            $"Voice Booth encountered a problem. Any stopped recordings remain saved locally.\n\n{e.Exception.Message}",
            "Voice Booth",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.Write(AppLogLevel.Critical, 1101, "Unhandled application exception.", e.ExceptionObject as Exception,
            new Dictionary<string, object?> { ["isTerminating"] = e.IsTerminating });
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Write(AppLogLevel.Error, 1102, "Unobserved background task exception.", e.Exception);
        e.SetObserved();
    }

    private static RollingFileLogger CreateLogger(ApplicationPaths paths)
    {
        try
        {
            return new RollingFileLogger(paths.LogDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return new RollingFileLogger(Path.Combine(paths.LocalDataRoot, "logs"));
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ApplyLoggingPolicy(RollingFileLogger? logger, VoiceBoothKiosk.Domain.KioskSettings settings)
    {
        if (logger is null)
        {
            return;
        }

        var level = Enum.TryParse<AppLogLevel>(settings.Diagnostics.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : AppLogLevel.Information;
        logger.UpdatePolicy(level, settings.Diagnostics.LogRetentionDays);
    }
}
