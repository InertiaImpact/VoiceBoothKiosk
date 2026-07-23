using System.IO;
using System.Runtime.InteropServices;

namespace VoiceBoothKiosk.Config;

internal static class DesktopShortcutService
{
    public static string Create(string targetPath, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var fullTargetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(fullTargetPath))
        {
            throw new FileNotFoundException("The recorder executable could not be found.", fullTargetPath);
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            throw new DirectoryNotFoundException("Windows did not provide a Desktop folder for this account.");
        }

        var shortcutPath = Path.Combine(desktop, "Voice Booth Kiosk.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("Windows shortcut support is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows shortcut support could not be started.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath])
                ?? throw new InvalidOperationException("Windows did not create the shortcut object.");
            var shortcutType = shortcut.GetType();
            SetProperty(shortcutType, shortcut, "TargetPath", fullTargetPath);
            SetProperty(shortcutType, shortcut, "Arguments", arguments ?? string.Empty);
            SetProperty(shortcutType, shortcut, "WorkingDirectory", Path.GetDirectoryName(fullTargetPath) ?? AppContext.BaseDirectory);
            SetProperty(shortcutType, shortcut, "Description", "Launch Voice Booth Kiosk");
            SetProperty(shortcutType, shortcut, "IconLocation", $"{fullTargetPath},0");
            shortcutType.InvokeMember(
                "Save",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: null);
            return shortcutPath;
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    private static void SetProperty(Type type, object target, string name, object value) =>
        type.InvokeMember(
            name,
            System.Reflection.BindingFlags.SetProperty,
            binder: null,
            target: target,
            args: [value]);

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
