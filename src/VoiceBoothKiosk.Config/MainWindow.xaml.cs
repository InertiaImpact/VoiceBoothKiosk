using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media;
using VoiceBoothKiosk.Application;
using VoiceBoothKiosk.Infrastructure.Configuration;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Config;

public partial class MainWindow : Window
{
    private readonly JsonConfigurationService _configuration;
    private readonly ConfigurationPresetService _presets;
    private readonly ManagedLogoService _logos;
    private SettingsEditorViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var machineRoot = Environment.GetEnvironmentVariable("VOICEBOOTH_MACHINE_DATA_ROOT");
        var localRoot = Environment.GetEnvironmentVariable("VOICEBOOTH_LOCAL_DATA_ROOT");
        var paths = new ApplicationPaths(machineRoot, localRoot);
        _configuration = new JsonConfigurationService(paths);
        _presets = new ConfigurationPresetService(paths);
        _logos = new ManagedLogoService(paths);
        ConfigPathText.Text = _configuration.Paths.MachineSettingsPath;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        SetBusy(true, "Loading configuration…", null);
        try
        {
            var settings = await _configuration.LoadAsync();
            SetViewModel(new SettingsEditorViewModel(settings));
            SetStatus("Configuration loaded", "Changes remain local to this window until Save and apply is selected.", isError: false);
        }
        catch (Exception exception)
        {
            SetViewModel(new SettingsEditorViewModel(_configuration.RestoreDefaults()));
            SetStatus("The saved configuration could not be loaded", exception.Message + " Defaults are shown but have not been saved.", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var settings)) return;
        SetBusy(true, "Saving configuration…", null);
        try
        {
            await _configuration.SaveAsync(settings!);
            SetViewModel(new SettingsEditorViewModel(await _configuration.LoadAsync()));
            SetStatus("Saved and ready", $"Atomic update completed at {DateTime.Now:t}. The previous file is retained as a backup.", isError: false);
        }
        catch (Exception exception)
        {
            SetStatus("Configuration was not saved", exception.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var settings)) return;
        var result = _configuration.Validate(settings!);
        ShowValidation(result);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Discard unsaved changes and reload the saved configuration?", "Reload configuration",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ReloadAsync();
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace the editor values with safe defaults? Nothing is written until you save.", "Restore defaults",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetViewModel(new SettingsEditorViewModel(_configuration.RestoreDefaults()));
        SetStatus("Defaults restored in the editor", "Review the destination path, then select Save and apply.", isError: false);
    }

    private async void ExportPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var settings)) return;
        var validation = _configuration.Validate(settings!);
        if (!validation.IsValid)
        {
            ShowValidation(validation);
            return;
        }

        var safeTitle = VoiceBoothKiosk.Domain.FileNameSanitizer.ToSafeComponent(
            settings!.Branding.ApplicationTitle,
            "Voice-Booth");
        var dialog = new SaveFileDialog
        {
            Title = "Export Voice Booth preset",
            Filter = "Voice Booth preset|*.voicebooth.zip|ZIP archive|*.zip",
            FileName = $"{safeTitle}-preset.voicebooth.zip",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true, "Exporting preset…", "The configuration and logo are being packaged into one portable ZIP file.");
        try
        {
            var result = await _presets.ExportAsync(settings, dialog.FileName);
            SetStatus(
                "Preset exported",
                result.IncludedLogo
                    ? $"Saved {result.ArchivePath} with the configured logo included."
                    : $"Saved {result.ArchivePath}.",
                isError: false);
        }
        catch (Exception exception)
        {
            SetStatus("Preset was not exported", exception.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportPreset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Voice Booth preset",
            Filter = "Voice Booth preset|*.voicebooth.zip;*.zip|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(
                this,
                "Replace the values currently shown in the editor with this preset? The kiosk configuration will not change until you select Save and apply.",
                "Import preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "Importing preset…", "The package is being validated and its logo is being installed locally.");
        try
        {
            var result = await _presets.ImportAsync(dialog.FileName);
            var validation = _configuration.Validate(result.Settings);
            if (!validation.IsValid)
            {
                ShowValidation(validation);
                return;
            }

            SetViewModel(new SettingsEditorViewModel(result.Settings));
            SetStatus(
                "Preset loaded into the editor",
                result.IncludedLogo
                    ? "The embedded logo was installed locally. Review machine-specific devices and destinations, then select Save and apply."
                    : "Review machine-specific devices and destinations, then select Save and apply.",
                isError: false);
        }
        catch (Exception exception)
        {
            SetStatus("Preset was not imported", exception.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BrowseLogo_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose a local logo image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true, "Copying logo…", "The selected image is being copied into the station's managed assets folder.");
        try
        {
            var result = await _logos.CopyIntoManagedStorageAsync(dialog.FileName);
            _viewModel.LogoPath = result.ManagedPath;
            SetViewModel(_viewModel);
            SetStatus(
                "Logo copied locally",
                "The managed copy will remain available if the original image is removed. Select Save and apply to activate it.",
                isError: false);
        }
        catch (Exception exception)
        {
            SetStatus("Logo was not selected", exception.Message, isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AddContentBlock_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ContentBlocks.Add(new ContentBlockRow
        {
            Id = $"recording.extra.custom-{(_viewModel.ContentBlocks.Count + 1):00}",
            Style = "Body",
            IsVisible = true
        });
    }

    private void RemoveContentBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (_viewModel.SelectedContentBlock is { } selected)
            _viewModel.ContentBlocks.Remove(selected);
    }

    private void AddDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var row = new DestinationRow { Id = $"destination-{Guid.NewGuid():N}" };
        _viewModel.Destinations.Add(row);
        _viewModel.SelectedDestination = row;
    }

    private void RemoveDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedDestination is not { } selected) return;
        if (MessageBox.Show(this, $"Remove destination '{selected.Label}'?", "Remove destination",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _viewModel.Destinations.Remove(selected);
        if (selected.IsDefault && _viewModel.Destinations.Count > 0)
            _viewModel.SetDefaultDestination(_viewModel.Destinations[0]);
        SetViewModel(_viewModel);
    }

    private void SetDefaultDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedDestination is not { } selected) return;
        _viewModel.SetDefaultDestination(selected);
        SetViewModel(_viewModel);
        SetStatus("Default destination changed in the editor", "Save and apply to make it active.", isError: false);
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedDestination is not { } selected)
        {
            SetStatus("Select a destination first", null, isError: true);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose an approved export root", Multiselect = false };
        if (Directory.Exists(selected.RootPath)) dialog.InitialDirectory = selected.RootPath;
        if (dialog.ShowDialog(this) == true)
        {
            selected.RootPath = dialog.FolderName;
            SetViewModel(_viewModel);
        }
    }

    private void CheckDestination_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedDestination is not { } selected)
        {
            SetStatus("Select a destination to check", null, isError: true);
            return;
        }

        var inspection = DestinationInspector.Inspect(selected.RootPath, testWrite: true);
        if (inspection.Exists && inspection.CanWrite)
            SetStatus("Destination is writable", $"{inspection.NormalizedPath} · {FormatBytes(inspection.AvailableBytes)} available", isError: false);
        else
            SetStatus("Destination check failed", inspection.Error ?? "The selected folder is not writable.", isError: true);
    }

    private bool TryBuildSettings(out VoiceBoothKiosk.Domain.KioskSettings? settings)
    {
        settings = null;
        if (_viewModel is null) return false;
        try
        {
            settings = _viewModel.ToSettings();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or FormatException)
        {
            SetStatus("A value could not be read", exception.Message, isError: true);
            return false;
        }
    }

    private void ShowValidation(SettingsValidationResult result)
    {
        if (result.IsValid)
        {
            SetStatus("Configuration is valid", "All values are internally consistent. Destination availability is checked separately.", isError: false);
            return;
        }
        SetStatus($"Fix {result.Errors.Count} validation issue{(result.Errors.Count == 1 ? string.Empty : "s")}",
            string.Join(Environment.NewLine, result.Errors.Take(8).Select(error => $"{error.Path}: {error.Message}")), isError: true);
    }

    private void SetViewModel(SettingsEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = null;
        DataContext = viewModel;
    }

    private void SetBusy(bool busy, string? heading = null, string? detail = null)
    {
        SaveButton.IsEnabled = !busy;
        ImportPresetButton.IsEnabled = !busy;
        ExportPresetButton.IsEnabled = !busy;
        if (heading is not null) SetStatus(heading, detail, isError: false);
    }

    private void SetStatus(string heading, string? detail, bool isError)
    {
        StatusText.Text = heading;
        StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isError ? "#B42318" : "#176B45"));
        StatusDetailText.Text = detail ?? string.Empty;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "free space unknown";
        return bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.0} TB"
            : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
            : $"{bytes / (double)(1L << 20):0} MB";
    }
}
