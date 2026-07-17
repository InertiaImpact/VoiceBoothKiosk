using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Persistence;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Configuration;

public sealed record ConfigurationPresetExportResult(string ArchivePath, bool IncludedLogo);

public sealed record ConfigurationPresetImportResult(
    KioskSettings Settings,
    bool IncludedLogo,
    string? InstalledLogoPath);

/// <summary>
/// Creates and reads portable Voice Booth ZIP presets. Only the known manifest and optional
/// logo entry are read; archive paths are never extracted directly to the filesystem.
/// </summary>
public sealed class ConfigurationPresetService
{
    private const int PresetSchemaVersion = 1;
    private const long MaximumManifestBytes = 2L * 1024 * 1024;
    private const long MaximumLogoBytes = 25L * 1024 * 1024;
    private const string ProductName = "VoiceBoothKiosk";
    private const string ManifestEntryName = "preset.json";
    private const string LogoEntryPrefix = "assets/";
    private static readonly HashSet<string> AllowedLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif"
    };

    private readonly ApplicationPaths paths;
    private readonly JsonSerializerOptions jsonOptions;

    public ConfigurationPresetService(ApplicationPaths paths, JsonSerializerOptions? jsonOptions = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.jsonOptions = jsonOptions ?? AtomicJsonFile.CreateDefaultOptions();
    }

    public async Task<ConfigurationPresetExportResult> ExportAsync(
        KioskSettings settings,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var portableSettings = settings with
        {
            Branding = settings.Branding with { LogoPath = null }
        };
        ValidateSettings(portableSettings);

        var fullArchivePath = Path.GetFullPath(archivePath);
        var parent = Path.GetDirectoryName(fullArchivePath)
            ?? throw new ArgumentException("The preset path must include a parent directory.", nameof(archivePath));
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"The preset destination folder does not exist: {parent}");
        }

        string? logoPath = null;
        string? logoEntryName = null;
        string? logoSha256 = null;
        if (!string.IsNullOrWhiteSpace(settings.Branding.LogoPath))
        {
            logoPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.Branding.LogoPath));
            var extension = ValidateLogoFile(logoPath);
            logoEntryName = $"{LogoEntryPrefix}logo{extension.ToLowerInvariant()}";
            logoSha256 = await ComputeFileHashAsync(logoPath, cancellationToken).ConfigureAwait(false);
        }

        var manifest = new ConfigurationPresetManifest
        {
            SchemaVersion = PresetSchemaVersion,
            Product = ProductName,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Settings = portableSettings,
            LogoEntryName = logoEntryName,
            LogoSha256 = logoSha256,
        };
        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(fullArchivePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                    await using (var manifestStream = manifestEntry.Open())
                    {
                        await JsonSerializer.SerializeAsync(
                            manifestStream,
                            manifest,
                            jsonOptions,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (logoPath is not null && logoEntryName is not null)
                    {
                        var logoEntry = archive.CreateEntry(logoEntryName, CompressionLevel.Optimal);
                        await using var logoOutput = logoEntry.Open();
                        await using var logoInput = new FileStream(
                            logoPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            64 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await logoInput.CopyToAsync(logoOutput, cancellationToken).ConfigureAwait(false);
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullArchivePath, overwrite: true);
            return new ConfigurationPresetExportResult(fullArchivePath, logoPath is not null);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public async Task<ConfigurationPresetImportResult> ImportAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullArchivePath = Path.GetFullPath(archivePath);
        await using var input = new FileStream(
            fullArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntries = archive.Entries
            .Where(entry => string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
            .ToArray();
        if (manifestEntries.Length != 1 || manifestEntries[0].Length <= 0 || manifestEntries[0].Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The preset must contain one valid preset.json manifest.");
        }

        ConfigurationPresetManifest manifest;
        await using (var manifestStream = manifestEntries[0].Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ConfigurationPresetManifest>(
                manifestStream,
                jsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The preset manifest is empty or invalid.");
        }

        if (manifest.SchemaVersion != PresetSchemaVersion ||
            !string.Equals(manifest.Product, ProductName, StringComparison.Ordinal) ||
            manifest.Settings is null ||
            manifest.Settings.Branding is null)
        {
            throw new InvalidDataException("The preset was not created by a supported Voice Booth version.");
        }

        var portableSettings = manifest.Settings with
        {
            Branding = manifest.Settings.Branding with { LogoPath = null }
        };
        ValidateSettings(portableSettings);

        string? installedLogoPath = null;
        var hasLogoMetadata = !string.IsNullOrWhiteSpace(manifest.LogoEntryName) ||
                              !string.IsNullOrWhiteSpace(manifest.LogoSha256);
        if (hasLogoMetadata)
        {
            if (string.IsNullOrWhiteSpace(manifest.LogoEntryName) ||
                string.IsNullOrWhiteSpace(manifest.LogoSha256) ||
                !IsSafeLogoEntryName(manifest.LogoEntryName))
            {
                throw new InvalidDataException("The preset logo metadata is incomplete or unsafe.");
            }

            var logoEntries = archive.Entries
                .Where(entry => string.Equals(entry.FullName, manifest.LogoEntryName, StringComparison.Ordinal))
                .ToArray();
            if (logoEntries.Length != 1 || logoEntries[0].Length <= 0 || logoEntries[0].Length > MaximumLogoBytes)
            {
                throw new InvalidDataException("The preset logo is missing, duplicated, empty, or too large.");
            }

            installedLogoPath = await InstallLogoAsync(
                logoEntries[0],
                manifest.LogoSha256,
                cancellationToken).ConfigureAwait(false);
        }

        var importedSettings = portableSettings with
        {
            Branding = portableSettings.Branding with { LogoPath = installedLogoPath }
        };
        ValidateSettings(importedSettings);
        return new ConfigurationPresetImportResult(
            importedSettings,
            installedLogoPath is not null,
            installedLogoPath);
    }

    private async Task<string> InstallLogoAsync(
        ZipArchiveEntry entry,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        paths.EnsureMachineDirectories();
        Directory.CreateDirectory(paths.MachineAssetsDirectory);
        var extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
        var temporaryPath = Path.Combine(paths.MachineAssetsDirectory, $".preset-logo-{Guid.NewGuid():N}.tmp");
        try
        {
            string actualHash;
            await using (var source = entry.Open())
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
                        throw new InvalidDataException("The preset logo exceeds the maximum allowed size.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The preset logo failed its integrity check.");
            }

            var finalPath = Path.Combine(
                paths.MachineAssetsDirectory,
                $"preset-logo-{actualHash[..16].ToLowerInvariant()}{extension}");
            if (File.Exists(finalPath))
            {
                var existingHash = await ComputeFileHashAsync(finalPath, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existingHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(temporaryPath);
                }
                else
                {
                    File.Move(temporaryPath, finalPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, finalPath);
            }
            return finalPath;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string ValidateLogoFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The configured logo file does not exist.", path);
        }

        var extension = Path.GetExtension(path);
        if (!AllowedLogoExtensions.Contains(extension))
        {
            throw new InvalidDataException("Preset logos must be PNG, JPG, JPEG, BMP, or GIF files.");
        }

        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumLogoBytes)
        {
            throw new InvalidDataException("The configured logo file is empty or larger than 25 MB.");
        }
        return extension;
    }

    private static bool IsSafeLogoEntryName(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (!string.Equals(normalized, entryName, StringComparison.Ordinal) ||
            !normalized.StartsWith(LogoEntryPrefix, StringComparison.Ordinal) ||
            normalized.Count(character => character == '/') != 1)
        {
            return false;
        }

        var fileName = normalized[LogoEntryPrefix.Length..];
        return !string.IsNullOrWhiteSpace(fileName) &&
               string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
               AllowedLogoExtensions.Contains(Path.GetExtension(fileName));
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

    private static void ValidateSettings(KioskSettings settings)
    {
        var shared = VoiceBoothKiosk.Application.KioskSettingsValidator.Validate(settings);
        var infrastructure = InfrastructureSettingsValidator.Validate(settings);
        var errors = shared.Errors.Concat(infrastructure.Errors)
            .DistinctBy(error => (error.Path, error.Message))
            .ToArray();
        if (errors.Length > 0)
        {
            throw new SettingsValidationException(errors);
        }
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

    private sealed record ConfigurationPresetManifest
    {
        public int SchemaVersion { get; init; }
        public string Product { get; init; } = string.Empty;
        public DateTimeOffset ExportedAtUtc { get; init; }
        public KioskSettings? Settings { get; init; }
        public string? LogoEntryName { get; init; }
        public string? LogoSha256 { get; init; }
    }
}
