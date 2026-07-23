namespace VoiceBoothKiosk.Domain;

public static class InstructionTextFormatter
{
    /// <summary>
    /// Converts portable instruction escape sequences into display line breaks.
    /// Actual CR/LF line endings entered in a multiline editor remain supported.
    /// </summary>
    public static string ForDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}
