using VoiceBoothKiosk.Infrastructure.Configuration;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Tests;

public sealed class ManagedLogoTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "VoiceBoothKiosk.ManagedLogoTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Selected_logo_is_copied_to_managed_storage_and_survives_source_removal()
    {
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "removable-station-logo.PNG");
        var expectedBytes = Enumerable.Range(0, 1024).Select(index => (byte)(index % 239)).ToArray();
        await File.WriteAllBytesAsync(sourcePath, expectedBytes);
        var paths = new ApplicationPaths(
            Path.Combine(root, "machine"),
            Path.Combine(root, "local"));
        var service = new ManagedLogoService(paths);

        var result = await service.CopyIntoManagedStorageAsync(sourcePath);
        File.Delete(sourcePath);

        Assert.StartsWith(paths.MachineAssetsDirectory, result.ManagedPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".png", result.ManagedPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ManagedPath));
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(result.ManagedPath));
    }

    [Fact]
    public async Task Selecting_the_same_logo_reuses_the_content_addressed_managed_copy()
    {
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "station-logo.jpg");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5, 6]);
        var paths = new ApplicationPaths(
            Path.Combine(root, "machine"),
            Path.Combine(root, "local"));
        var service = new ManagedLogoService(paths);

        var first = await service.CopyIntoManagedStorageAsync(sourcePath);
        var second = await service.CopyIntoManagedStorageAsync(sourcePath);

        Assert.Equal(first, second);
        Assert.Single(Directory.EnumerateFiles(paths.MachineAssetsDirectory, "station-logo-*.jpg"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
