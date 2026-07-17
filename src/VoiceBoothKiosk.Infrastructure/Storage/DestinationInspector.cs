namespace VoiceBoothKiosk.Infrastructure.Storage;

public sealed record DestinationInspection(
    string NormalizedPath,
    bool Exists,
    bool CanWrite,
    long AvailableBytes,
    string? Error)
{
    public bool IsUsable(long minimumFreeBytes) => Exists && CanWrite && AvailableBytes >= minimumFreeBytes;
}

public static class DestinationInspector
{
    public static string NormalizeApprovedRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathFullyQualified(expanded))
        {
            throw new ArgumentException("Destination paths must be absolute or UNC paths.", nameof(path));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    public static DestinationInspection Inspect(string path, bool testWrite = true)
    {
        string normalized;
        try
        {
            normalized = NormalizeApprovedRoot(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new DestinationInspection(path, false, false, 0, exception.Message);
        }

        if (!Directory.Exists(normalized))
        {
            return new DestinationInspection(normalized, false, false, 0, "Folder does not exist.");
        }

        var canWrite = !testWrite;
        string? error = null;
        if (testWrite)
        {
            var probe = Path.Combine(normalized, $".voicebooth-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough)) { }
                File.Delete(probe);
                canWrite = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception.Message;
                TryDelete(probe);
            }
        }

        long availableBytes = 0;
        try
        {
            var root = Path.GetPathRoot(normalized);
            if (!string.IsNullOrEmpty(root))
            {
                availableBytes = new DriveInfo(root).AvailableFreeSpace;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error ??= $"Free space could not be determined: {exception.Message}";
        }

        return new DestinationInspection(normalized, true, canWrite, availableBytes, error);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
