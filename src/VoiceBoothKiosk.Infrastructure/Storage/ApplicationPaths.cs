namespace VoiceBoothKiosk.Infrastructure.Storage;

/// <summary>All well-known storage locations used by the recorder and configuration editor.</summary>
public sealed class ApplicationPaths
{
    public const string ProductDirectoryName = "VoiceBoothKiosk";

    public ApplicationPaths(string? machineDataRoot = null, string? localDataRoot = null)
    {
        MachineDataRoot = Normalize(machineDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductDirectoryName));
        LocalDataRoot = Normalize(localDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductDirectoryName));
    }

    public string MachineDataRoot { get; }
    public string MachineConfigDirectory => Path.Combine(MachineDataRoot, "config");
    public string MachineSettingsPath => Path.Combine(MachineConfigDirectory, "machine-settings.json");
    public string MachineAssetsDirectory => Path.Combine(MachineConfigDirectory, "assets");
    public string LogDirectory => Path.Combine(MachineDataRoot, "logs");

    public string LocalDataRoot { get; }
    public string StateDirectory => Path.Combine(LocalDataRoot, "state");
    public string UserSettingsPath => Path.Combine(StateDirectory, "user-settings.json");
    public string WorkDirectory => Path.Combine(LocalDataRoot, "work");
    public string QuarantineDirectory => Path.Combine(LocalDataRoot, "quarantine");

    public string GetSessionDirectory(Guid sessionId) => Path.Combine(WorkDirectory, sessionId.ToString("D"));
    public string GetSessionManifestPath(Guid sessionId) => Path.Combine(GetSessionDirectory(sessionId), "session.json");
    public string GetClipSegmentDirectory(Guid sessionId, Guid clipId) =>
        Path.Combine(GetSessionDirectory(sessionId), "segments", clipId.ToString("D"));
    public string GetSegmentPath(Guid sessionId, Guid clipId, Guid segmentId, bool partial = false) =>
        Path.Combine(GetClipSegmentDirectory(sessionId, clipId), $"{segmentId:D}.wav{(partial ? ".partial" : string.Empty)}");
    public string GetPeaksPath(Guid sessionId, Guid segmentId) =>
        Path.Combine(GetSessionDirectory(sessionId), "peaks", $"{segmentId:D}.peaks");
    public string GetTrashDirectory(Guid sessionId) => Path.Combine(GetSessionDirectory(sessionId), "trash");
    public string GetQuarantinedSessionDirectory(Guid sessionId) => Path.Combine(QuarantineDirectory, sessionId.ToString("D"));

    public void EnsureMachineDirectories()
    {
        Directory.CreateDirectory(MachineConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public void EnsureLocalDirectories()
    {
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(WorkDirectory);
        Directory.CreateDirectory(QuarantineDirectory);
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)));
    }
}
