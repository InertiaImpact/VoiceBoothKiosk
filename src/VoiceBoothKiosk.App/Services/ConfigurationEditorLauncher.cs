using System.Diagnostics;
using System.IO;

namespace VoiceBoothKiosk.App.Services;

public interface IConfigurationEditorLauncher
{
    Task OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Locates and opens the companion configuration editor, then waits for it to close.</summary>
public sealed class ConfigurationEditorLauncher : IConfigurationEditorLauncher
{
    private const string ExecutableName = "VoiceBoothKiosk.Config.exe";

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var executablePath = FindExecutable();
        if (executablePath is null)
        {
            throw new FileNotFoundException(
                $"{ExecutableName} was not found. Install the companion 'config' folder beside the recorder, " +
                "or set VOICEBOOTH_CONFIG_EXE to its full path.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            Arguments = "--opened-from-recorder",
            UseShellExecute = true
        };

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
