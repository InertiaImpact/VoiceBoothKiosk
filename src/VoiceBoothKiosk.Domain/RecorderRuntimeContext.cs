using System.Text;
using System.Text.Json;

namespace VoiceBoothKiosk.Domain;

/// <summary>
/// A short-lived snapshot passed from the recorder to its configuration editor.
/// </summary>
public sealed record RecorderRuntimeContext
{
    public const string EnvironmentVariableName = "VOICEBOOTH_RECORDER_RUNTIME_CONTEXT";

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsReady { get; init; }
    public string OverallStatus { get; init; } = string.Empty;
    public string InputStatus { get; init; } = string.Empty;
    public string StorageStatus { get; init; } = string.Empty;
    public string EncoderStatus { get; init; } = string.Empty;
    public string KeepAwakeStatus { get; init; } = string.Empty;
    public string RecorderExecutablePath { get; init; } = string.Empty;
    public string RecorderArguments { get; init; } = string.Empty;
    public string LaunchMode { get; init; } = "Standard";

    public string ToEnvironmentValue()
    {
        var json = JsonSerializer.Serialize(this);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static RecorderRuntimeContext? FromEnvironment()
    {
        var encoded = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize<RecorderRuntimeContext>(json);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    public static (string Arguments, string Mode) GetPublicLaunchMode(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        if (values.Contains("--supervised-kiosk", StringComparer.OrdinalIgnoreCase))
        {
            return ("--supervised-kiosk", "Supervised full-screen");
        }
        if (values.Contains("--windowed", StringComparer.OrdinalIgnoreCase))
        {
            return ("--windowed", "Windowed test");
        }
        return (string.Empty, "Standard");
    }
}
