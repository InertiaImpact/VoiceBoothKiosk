using System.IO.Compression;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Configuration;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Tests;

public sealed class ConfigurationPresetTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "VoiceBoothKiosk.PresetTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preset_round_trip_preserves_configuration_and_installs_embedded_logo()
    {
        Directory.CreateDirectory(root);
        var logoPath = Path.Combine(root, "station-logo.PNG");
        var logoBytes = Enumerable.Range(0, 512).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(logoPath, logoBytes);
        var destinationRoot = Path.Combine(root, "exports");
        Directory.CreateDirectory(destinationRoot);
        var settings = CreateSettings(logoPath, destinationRoot);
        var paths = new ApplicationPaths(
            Path.Combine(root, "machine"),
            Path.Combine(root, "local"));
        var service = new ConfigurationPresetService(paths);
        var presetPath = Path.Combine(root, "station.voicebooth.zip");

        var exported = await service.ExportAsync(settings, presetPath);

        Assert.True(exported.IncludedLogo);
        using (var archive = ZipFile.OpenRead(presetPath))
        {
            Assert.NotNull(archive.GetEntry("preset.json"));
            Assert.NotNull(archive.GetEntry("assets/logo.png"));
            using var manifestReader = new StreamReader(archive.GetEntry("preset.json")!.Open());
            var manifest = await manifestReader.ReadToEndAsync();
            Assert.DoesNotContain(logoPath, manifest, StringComparison.OrdinalIgnoreCase);
        }

        var imported = await service.ImportAsync(presetPath);

        Assert.True(imported.IncludedLogo);
        Assert.NotNull(imported.InstalledLogoPath);
        Assert.StartsWith(paths.MachineAssetsDirectory, imported.InstalledLogoPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(logoBytes, await File.ReadAllBytesAsync(imported.InstalledLogoPath));
        Assert.Equal(imported.InstalledLogoPath, imported.Settings.Branding.LogoPath);
        Assert.Equal("Multi-station Voice Booth", imported.Settings.Branding.ApplicationTitle);
        Assert.Equal("#112233", imported.Settings.Branding.PrimaryColor);
        Assert.Equal("#445566", imported.Settings.Branding.AccentColor);
        Assert.Equal("#778899", imported.Settings.Layout.WaveformColor);
        Assert.Equal(settings.ContentBlocks, imported.Settings.ContentBlocks);
        Assert.Equal(settings.Destinations, imported.Settings.Destinations);
        Assert.Equal(settings.DefaultDestinationId, imported.Settings.DefaultDestinationId);
    }

    [Fact]
    public async Task Preset_import_rejects_a_logo_that_does_not_match_its_manifest_hash()
    {
        Directory.CreateDirectory(root);
        var logoPath = Path.Combine(root, "station-logo.png");
        await File.WriteAllBytesAsync(logoPath, [1, 2, 3, 4, 5]);
        var destinationRoot = Path.Combine(root, "exports");
        Directory.CreateDirectory(destinationRoot);
        var paths = new ApplicationPaths(
            Path.Combine(root, "machine"),
            Path.Combine(root, "local"));
        var service = new ConfigurationPresetService(paths);
        var presetPath = Path.Combine(root, "tampered.voicebooth.zip");
        await service.ExportAsync(CreateSettings(logoPath, destinationRoot), presetPath);

        using (var archive = ZipFile.Open(presetPath, ZipArchiveMode.Update))
        {
            archive.GetEntry("assets/logo.png")!.Delete();
            var replacement = archive.CreateEntry("assets/logo.png");
            await using var output = replacement.Open();
            await output.WriteAsync(new byte[] { 9, 9, 9, 9 });
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(presetPath));

        Assert.Contains("integrity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(paths.MachineAssetsDirectory) &&
                     Directory.EnumerateFiles(paths.MachineAssetsDirectory, "preset-logo-*").Any());
    }

    private static KioskSettings CreateSettings(string logoPath, string destinationRoot)
    {
        var destination = new DestinationPreset
        {
            Id = "shared-output",
            Label = "Shared station output",
            RootPath = destinationRoot,
            MinimumFreeBytes = 0
        };
        return KioskSettings.CreateDefault() with
        {
            Branding = new BrandingSettings
            {
                ApplicationTitle = "Multi-station Voice Booth",
                ApplicationSubtitle = "Shared preset",
                LogoPath = logoPath,
                PrimaryColor = "#112233",
                AccentColor = "#445566",
                BackgroundColor = "#08111F"
            },
            Layout = new LayoutSettings
            {
                WaveformColor = "#778899",
                WaveformBackgroundColor = "#07111F"
            },
            ContentBlocks =
            [
                new ContentBlock
                {
                    Id = "welcome.instructions",
                    Text = "Preset instructions",
                    Style = "Body"
                }
            ],
            Destinations = [destination],
            DefaultDestinationId = destination.Id
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
