using System.Diagnostics;
using VoiceBoothKiosk.Config;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Tests;

public sealed class SettingsEditorViewModelTests
{
    [Fact]
    public void Visible_release_badge_uses_the_compiled_product_version()
    {
        Assert.Equal("1.1.2", VoiceBoothRelease.Version);
        Assert.Equal("v1.1.2", VoiceBoothRelease.DisplayVersion);
        Assert.Equal(
            "1.1.2",
            FileVersionInfo.GetVersionInfo(typeof(VoiceBoothRelease).Assembly.Location).ProductVersion);
    }

    [Fact]
    public void Keep_awake_round_trips_through_the_settings_editor_mapper()
    {
        var original = KioskSettings.CreateDefault() with
        {
            Kiosk = KioskSettings.CreateDefault().Kiosk with { KeepAwake = false }
        };
        var viewModel = new SettingsEditorViewModel(original);

        Assert.False(viewModel.KeepAwake);
        Assert.False(viewModel.ToSettings().Kiosk.KeepAwake);

        viewModel.KeepAwake = true;

        Assert.True(viewModel.ToSettings().Kiosk.KeepAwake);
    }

    [Fact]
    public void Editor_stamps_the_running_release_version_instead_of_stale_configuration_metadata()
    {
        var viewModel = new SettingsEditorViewModel(
            KioskSettings.CreateDefault() with { ApplicationVersion = "1.0.0" });

        var result = viewModel.ToSettings();

        Assert.Equal(VoiceBoothRelease.Version, viewModel.ApplicationVersion);
        Assert.Equal(VoiceBoothRelease.Version, result.ApplicationVersion);
    }
}
