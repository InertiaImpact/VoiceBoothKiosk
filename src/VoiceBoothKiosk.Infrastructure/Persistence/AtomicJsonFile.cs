using System.Text.Json;

namespace VoiceBoothKiosk.Infrastructure.Persistence;

/// <summary>
/// Reads and writes JSON without ever modifying the live file in-place. A successful
/// replacement retains the previous file as <c>.bak</c>; reads fall back to that copy
/// if the primary file is missing or malformed.
/// </summary>
public sealed class AtomicJsonFile
{
    private readonly JsonSerializerOptions _options;

    public AtomicJsonFile(JsonSerializerOptions? options = null)
    {
        _options = options ?? CreateDefaultOptions();
    }

    public JsonSerializerOptions SerializerOptions => _options;

    public async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            var missingPrimaryBackup = GetBackupPath(path);
            return File.Exists(missingPrimaryBackup)
                ? await ReadSingleAsync<T>(missingPrimaryBackup, cancellationToken).ConfigureAwait(false)
                : default;
        }

        try
        {
            return await ReadSingleAsync<T>(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableReadFailure(exception))
        {
            var backupPath = GetBackupPath(path);
            if (!File.Exists(backupPath))
            {
                throw new InvalidDataException($"The JSON file '{path}' could not be read and no backup is available.", exception);
            }

            try
            {
                return await ReadSingleAsync<T>(backupPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception backupException) when (IsRecoverableReadFailure(backupException))
            {
                throw new InvalidDataException(
                    $"Neither the JSON file '{path}' nor its backup could be read.",
                    new AggregateException(exception, backupException));
            }
        }
    }

    public async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The JSON path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = GetBackupPath(fullPath);

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            Replace(fullPath, temporaryPath, backupPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static string GetBackupPath(string path) => $"{path}.bak";

    public static JsonSerializerOptions CreateDefaultOptions() => new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private async Task<T?> ReadSingleAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false);
    }

    private static void Replace(string destinationPath, string temporaryPath, string backupPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            ReplacePortable(destinationPath, temporaryPath, backupPath);
        }
        catch (IOException)
        {
            // File.Replace is not available on every target filesystem (notably some shares).
            ReplacePortable(destinationPath, temporaryPath, backupPath);
        }
    }

    private static void ReplacePortable(string destinationPath, string temporaryPath, string backupPath)
    {
        File.Copy(destinationPath, backupPath, overwrite: true);
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static bool IsRecoverableReadFailure(Exception exception) =>
        exception is JsonException or IOException or UnauthorizedAccessException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: stale temp files are harmless and can be cleaned on maintenance.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
