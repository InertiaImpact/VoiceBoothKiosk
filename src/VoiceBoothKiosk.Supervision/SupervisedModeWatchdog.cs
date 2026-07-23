using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VoiceBoothKiosk.Supervision;

public static class SupervisedModeArguments
{
    public const string SupervisedKioskSwitch = "--supervised-kiosk";
    public const string WatchdogChildSwitch = "--supervised-watchdog-child";
    public const string CheckIntervalEnvironmentVariable = "VOICEBOOTH_WATCHDOG_INTERVAL_SECONDS";
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(2);

    public static bool ShouldRunSupervisor(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        return Contains(values, SupervisedKioskSwitch) && !Contains(values, WatchdogChildSwitch);
    }

    public static bool IsWatchdogChild(IEnumerable<string> arguments) =>
        Contains(arguments, WatchdogChildSwitch);

    public static IReadOnlyList<string> BuildChildArguments(IEnumerable<string> arguments)
    {
        var result = arguments
            .Where(argument => !string.Equals(argument, WatchdogChildSwitch, StringComparison.OrdinalIgnoreCase))
            .ToList();
        result.Add(WatchdogChildSwitch);
        return result;
    }

    public static TimeSpan ResolveCheckInterval(string? configuredSeconds)
    {
        if (double.TryParse(configuredSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 1 and <= 3600)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultCheckInterval;
    }

    private static bool Contains(IEnumerable<string> arguments, string value) =>
        arguments.Contains(value, StringComparer.OrdinalIgnoreCase);
}

public sealed class SupervisedModeWatchdog
{
    // The supervised recorder uses this otherwise-unused code only after the protected
    // four-second X completes. Every other exit is treated as unexpected.
    public const int IntentionalExitCode = 73;

    public async Task RunAsync(
        string recorderExecutablePath,
        IReadOnlyList<string> childArguments,
        TimeSpan checkInterval,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recorderExecutablePath))
        {
            throw new ArgumentException("A recorder executable path is required.", nameof(recorderExecutablePath));
        }
        if (checkInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(checkInterval), "The check interval must be positive.");
        }

        var fullRecorderPath = Path.GetFullPath(recorderExecutablePath);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process? recorder = null;
            try
            {
                recorder = StartRecorder(fullRecorderPath, childArguments);
                var exitTask = recorder.WaitForExitAsync(cancellationToken);
                while (!exitTask.IsCompleted)
                {
                    var tickTask = Task.Delay(checkInterval, cancellationToken);
                    if (await Task.WhenAny(exitTask, tickTask).ConfigureAwait(false) == exitTask)
                    {
                        break;
                    }

                    RestoreAndForeground(recorder);
                }

                await exitTask.ConfigureAwait(false);
                if (recorder.ExitCode == IntentionalExitCode)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A transient launch or process-query failure is retried on the same
                // bounded cadence as an unexpected recorder exit.
            }
            finally
            {
                recorder?.Dispose();
            }

            await Task.Delay(checkInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Process StartRecorder(string recorderExecutablePath, IReadOnlyList<string> childArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = recorderExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(recorderExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in childArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The supervised recorder process could not be started.");
    }

    private static void RestoreAndForeground(Process recorder)
    {
        if (!OperatingSystem.IsWindows() || recorder.HasExited)
        {
            return;
        }

        recorder.Refresh();
        var window = recorder.MainWindowHandle;
        if (window == IntPtr.Zero || !IsWindowVisible(window))
        {
            // The recorder intentionally hides its main window while Settings is open.
            return;
        }

        _ = ShowWindowAsync(window, ShowMaximized);
        _ = SetWindowPos(window, TopmostWindow, 0, 0, 0, 0, NoMove | NoSize | ShowWindow);
        _ = SetForegroundWindow(window);
    }

    private const int ShowMaximized = 3;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint ShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
