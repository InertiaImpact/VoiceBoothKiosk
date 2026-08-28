using System.Diagnostics;
using System.IO;
using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.App.Services;

public interface IConfigurationEditorLauncher
{
    Task OpenAsync(RecorderReadinessDetails readiness, CancellationToken cancellationToken = default);
}

public sealed record RecorderReadinessDetails(
    bool IsReady,
    string OverallStatus,
    string InputStatus,
    string StorageStatus,
    string EncoderStatus);

/// <summary>Locates and opens the companion configuration editor, then waits for it to close.</summary>
public sealed class ConfigurationEditorLauncher : IConfigurationEditorLauncher
{
    private const string ExecutableName = "VoiceBoothKiosk.Config.exe";
    private readonly Func<string> _keepAwakeStatusProvider;

    public ConfigurationEditorLauncher(Func<string>? keepAwakeStatusProvider = null)
    {
        _keepAwakeStatusProvider = keepAwakeStatusProvider ?? (() => "Status unavailable");
    }

    public async Task OpenAsync(RecorderReadinessDetails readiness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        var executablePath = FindExecutable();
        if (executablePath is null)
        {
            throw new FileNotFoundException(
                $"{ExecutableName} was not found. Install the companion 'config' folder beside the recorder, " +
                "or set VOICEBOOTH_CONFIG_EXE to its full path.");
        }

        var publicLaunch = RecorderRuntimeContext.GetPublicLaunchMode(Environment.GetCommandLineArgs().Skip(1));
        var runtimeContext = new RecorderRuntimeContext
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            IsReady = readiness.IsReady,
            OverallStatus = readiness.OverallStatus,
            InputStatus = readiness.InputStatus,
            StorageStatus = readiness.StorageStatus,
            EncoderStatus = readiness.EncoderStatus,
            KeepAwakeStatus = _keepAwakeStatusProvider(),
            RecorderExecutablePath = Environment.ProcessPath ?? string.Empty,
            RecorderArguments = publicLaunch.Arguments,
            LaunchMode = publicLaunch.Mode
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--opened-from-recorder");
        startInfo.Environment[RecorderRuntimeContext.EnvironmentVariableName] = runtimeContext.ToEnvironmentValue();

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The configuration editor could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The configuration editor closed with error code {process.ExitCode}. No recorder session was changed.");
        }
    }

    internal static string? FindExecutable()
    {
        var configuredPath = Environment.GetEnvironmentVariable("VOICEBOOTH_CONFIG_EXE");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
            return File.Exists(expanded) ? expanded : null;
        }

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var directCandidates = new[]
        {
            Path.Combine(baseDirectory, ExecutableName),
            Path.GetFullPath(Path.Combine(baseDirectory, "config", ExecutableName)),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "config", ExecutableName))
        };
        var directMatch = directCandidates.FirstOrDefault(File.Exists);
        if (directMatch is not null)
        {
            return directMatch;
        }

        // Development layout: walk up to the repository root, then choose the newest
        // non-obj editor apphost produced by a solution build.
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "VoiceBoothKiosk.sln")))
            {
                continue;
            }

            var binDirectory = Path.Combine(directory.FullName, "src", "VoiceBoothKiosk.Config", "bin");
            if (!Directory.Exists(binDirectory))
            {
                return null;
            }

            return Directory.EnumerateFiles(binDirectory, ExecutableName, SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        return null;
    }
}
