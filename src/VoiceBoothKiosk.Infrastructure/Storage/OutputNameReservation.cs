using System.Globalization;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Infrastructure.Storage;

public sealed record ClipOutputRequest(int Ordinal, string? CustomName);

public sealed record ReservedClipOutput(int Ordinal, string FileName, string FullPath);

/// <summary>
/// Holds an exclusive marker in the destination root so all output names are selected as
/// one set. Dispose abandons the reservation; Commit creates the reserved session folder.
/// </summary>
public sealed class OutputNameReservation : IDisposable
{
    private readonly FileStream _marker;
    private bool _committed;

    private OutputNameReservation(
        FileStream marker,
        string markerPath,
        string sessionDirectory,
        IReadOnlyList<ReservedClipOutput> clips)
    {
        _marker = marker;
        MarkerPath = markerPath;
        SessionDirectory = sessionDirectory;
        Clips = clips;
    }

    public string MarkerPath { get; }
    public string SessionDirectory { get; }
    public IReadOnlyList<ReservedClipOutput> Clips { get; }

    public static OutputNameReservation Reserve(
        string destinationRoot,
        string sessionName,
        DateTimeOffset sessionStartedLocal,
        IEnumerable<ClipOutputRequest> clips)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(clips);

        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(destinationRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Destination '{root}' does not exist.");
        }

        var safeSession = FileNameSanitizer.ToSafeComponent(sessionName, "Session");
        var date = sessionStartedLocal.ToString("MMddyyyy", CultureInfo.InvariantCulture);
        var clipRequests = clips.OrderBy(clip => clip.Ordinal).ToArray();

        for (var suffix = 1; suffix <= 9999; suffix++)
        {
            var baseFolderName = $"{safeSession}_{date}";
            var folderName = suffix == 1 ? baseFolderName : $"{baseFolderName}_{suffix:00}";
            var sessionDirectory = Path.Combine(root, folderName);
            var markerPath = Path.Combine(root, $".{folderName}.voicebooth-reservation");

            if (Directory.Exists(sessionDirectory))
            {
                continue;
            }

            FileStream marker;
            try
            {
                marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                if (Directory.Exists(sessionDirectory))
                {
                    marker.Dispose();
                    File.Delete(markerPath);
                    continue;
                }

                var outputs = ReserveClipNames(sessionDirectory, safeSession, date, clipRequests);
                return new OutputNameReservation(marker, markerPath, sessionDirectory, outputs);
            }
            catch
            {
                marker.Dispose();
                TryDelete(markerPath);
                throw;
            }
        }

        throw new IOException("Unable to reserve a unique session folder after 9,999 attempts.");
    }

    /// <summary>Reserves an already planned domain output set without changing any names.</summary>
    public static OutputNameReservation Reserve(string destinationRoot, OutputNamePlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(plan);
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(destinationRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Destination '{root}' does not exist.");
        ValidateSingleComponent(plan.SessionFolderName, "session folder");

        var sessionDirectory = Path.Combine(root, plan.SessionFolderName);
        if (Directory.Exists(sessionDirectory) || File.Exists(sessionDirectory))
            throw new IOException($"The destination session folder already exists: {sessionDirectory}");
        var markerPath = Path.Combine(root, $".{plan.SessionFolderName}.voicebooth-reservation");
        var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        try
        {
            if (Directory.Exists(sessionDirectory) || File.Exists(sessionDirectory))
                throw new IOException($"The destination session folder was created while names were being reserved: {sessionDirectory}");

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outputs = plan.ClipFiles.Select(clip =>
            {
                ValidateSingleComponent(clip.FileName, "MP3 file");
                if (!string.Equals(Path.GetExtension(clip.FileName), ".mp3", StringComparison.OrdinalIgnoreCase) || !used.Add(clip.FileName))
                    throw new ArgumentException($"The planned MP3 name is invalid or duplicated: {clip.FileName}", nameof(plan));
                return new ReservedClipOutput(clip.Ordinal, clip.FileName, Path.Combine(sessionDirectory, clip.FileName));
            }).ToArray();
            return new OutputNameReservation(marker, markerPath, sessionDirectory, outputs);
        }
        catch
        {
            marker.Dispose();
            TryDelete(markerPath);
            throw;
        }
    }

    public void Commit()
    {
        if (_committed)
        {
            return;
        }

        Directory.CreateDirectory(SessionDirectory);
        _committed = true;
        ReleaseMarker();
    }

    /// <summary>Atomically promotes a completed sibling staging directory while the name lock is held.</summary>
    public void CommitStagingDirectory(string stagingDirectory)
    {
        if (_committed) throw new InvalidOperationException("This output reservation is already committed.");
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var root = Path.GetDirectoryName(SessionDirectory)
            ?? throw new InvalidOperationException("The reservation has no destination root.");
        var staging = Path.GetFullPath(stagingDirectory);
        var relative = Path.GetRelativePath(root, staging);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative) || string.Equals(staging, SessionDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The staging directory must be a different child of the reserved destination root.", nameof(stagingDirectory));
        if (!Directory.Exists(staging)) throw new DirectoryNotFoundException($"Staging directory '{staging}' does not exist.");
        if (Directory.Exists(SessionDirectory) || File.Exists(SessionDirectory))
            throw new IOException($"The reserved destination already exists: {SessionDirectory}");

        Directory.Move(staging, SessionDirectory);
        _committed = true;
        ReleaseMarker();
    }

    public void Dispose() => ReleaseMarker();

    private static IReadOnlyList<ReservedClipOutput> ReserveClipNames(
        string sessionDirectory,
        string safeSession,
        string date,
        IReadOnlyList<ClipOutputRequest> clips)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ReservedClipOutput>(clips.Count);

        foreach (var clip in clips)
        {
            var component = string.IsNullOrWhiteSpace(clip.CustomName)
                ? clip.Ordinal.ToString("00", CultureInfo.InvariantCulture)
                : FileNameSanitizer.ToSafeComponent(clip.CustomName, $"Clip-{clip.Ordinal}");
            var baseName = $"{safeSession}_{component}";
            var counter = 1;
            string fileName;
            do
            {
                var disambiguator = counter == 1 ? string.Empty : $"_{counter:00}";
                fileName = $"{baseName}{disambiguator}_{date}.mp3";
                counter++;
            }
            while (!used.Add(fileName));

            results.Add(new ReservedClipOutput(clip.Ordinal, fileName, Path.Combine(sessionDirectory, fileName)));
        }

        return results;
    }

    private static void ValidateSingleComponent(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)
            || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) || value is "." or "..")
            throw new ArgumentException($"The {description} name is not a safe single path component.", nameof(value));
    }

    private void ReleaseMarker()
    {
        _marker.Dispose();
        TryDelete(MarkerPath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
