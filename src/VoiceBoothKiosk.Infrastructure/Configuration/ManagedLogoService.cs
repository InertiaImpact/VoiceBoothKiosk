using System.Security.Cryptography;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Configuration;

public sealed record ManagedLogoResult(string ManagedPath, string Sha256);

/// <summary>
/// Copies a station logo into machine-managed configuration storage so branding does not
/// depend on removable media, network availability, or the operator's original file.
/// </summary>
public sealed class ManagedLogoService
{
    public const long MaximumLogoBytes = 25L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif"
    };

    private readonly ApplicationPaths paths;

    public ManagedLogoService(ApplicationPaths paths)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<ManagedLogoResult> CopyIntoManagedStorageAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath));
        var extension = ValidateSource(fullSourcePath).ToLowerInvariant();

        paths.EnsureMachineDirectories();
        Directory.CreateDirectory(paths.MachineAssetsDirectory);
        var temporaryPath = Path.Combine(paths.MachineAssetsDirectory, $".logo-{Guid.NewGuid():N}.tmp");
        try
        {
            string actualHash;
            await using (var source = new FileStream(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total = checked(total + read);
                    if (total > MaximumLogoBytes)
                    {
                        throw new InvalidDataException("The selected logo is larger than 25 MB.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total == 0)
                {
                    throw new InvalidDataException("The selected logo file is empty.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            var finalPath = Path.Combine(
                paths.MachineAssetsDirectory,
                $"station-logo-{actualHash[..16].ToLowerInvariant()}{extension}");
            if (File.Exists(finalPath))
            {
                var existingHash = await ComputeFileHashAsync(finalPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(existingHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(temporaryPath, finalPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, finalPath);
            }

            return new ManagedLogoResult(finalPath, actualHash);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string ValidateSource(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected logo file does not exist.", path);
        }

        var extension = Path.GetExtension(path);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidDataException("Logos must be PNG, JPG, JPEG, BMP, or GIF files.");
        }

        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumLogoBytes)
        {
            throw new InvalidDataException("The selected logo file is empty or larger than 25 MB.");
        }

        return extension;
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
