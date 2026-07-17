using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBoothKiosk.Domain;

public static partial class FileNameSanitizer
{
    public const int DefaultMaximumLength = 80;
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string ToSafeComponent(string? value, string fallback, int maximumLength = DefaultMaximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        if (maximumLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        var builder = new StringBuilder(value?.Length ?? 0);
        foreach (var character in value ?? string.Empty)
        {
            builder.Append(char.IsControl(character) || InvalidCharacters.Contains(character) ? '-' : character);
        }

        var result = RepeatedWhitespace().Replace(builder.ToString(), "-");
        result = RepeatedSeparators().Replace(result, "-");
        result = result.Trim(' ', '.', '-', '_');
        result = TruncateTextElementSafe(result, maximumLength).TrimEnd(' ', '.');

        if (string.IsNullOrWhiteSpace(result) || IsReservedDeviceName(result))
        {
            result = CleanFallback(fallback, maximumLength);
        }

        return result;
    }

    public static bool IsReservedDeviceName(string value)
    {
        var baseName = value.Trim().TrimEnd('.').Split('.')[0];
        return ReservedDeviceName().IsMatch(baseName);
    }

    private static string CleanFallback(string fallback, int maximumLength)
    {
        var cleaned = RepeatedWhitespace().Replace(fallback.Trim(), "-");
        cleaned = RepeatedSeparators().Replace(cleaned, "-").Trim(' ', '.', '-', '_');
        cleaned = TruncateTextElementSafe(cleaned, maximumLength).TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) || IsReservedDeviceName(cleaned) ? "Item" : cleaned;
    }

    private static string TruncateTextElementSafe(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        var info = new StringInfo(value);
        var count = Math.Min(info.LengthInTextElements, maximumLength);
        var result = info.SubstringByTextElements(0, count);
        while (result.Length > maximumLength && count > 0)
        {
            result = info.SubstringByTextElements(0, --count);
        }

        return result;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedWhitespace();

    [GeneratedRegex(@"[-_]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSeparators();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CLOCK\$|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();
}

public sealed record ClipOutputName(Guid ClipId, int Ordinal, string FileName);

public sealed record OutputNamePlan(string SessionFolderName, IReadOnlyList<ClipOutputName> ClipFiles)
{
    public string GetFileName(Guid clipId) => ClipFiles.Single(item => item.ClipId == clipId).FileName;
}

public static class OutputNamePlanner
{
    public static OutputNamePlan Create(RecordingSession session, IEnumerable<string>? existingSessionFolderNames = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var existingFolders = new HashSet<string>(existingSessionFolderNames ?? [], StringComparer.OrdinalIgnoreCase);
        var date = session.StartedAtLocal.ToString("MMddyyyy", CultureInfo.InvariantCulture);
        var folderBase = $"{session.SafeName}_{date}";
        var folderName = MakeUnique(folderBase, existingFolders);

        var reservedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ClipOutputName>();
        foreach (var clip in session.ActiveClips.Where(clip => clip.HasAudio).OrderBy(clip => clip.Ordinal))
        {
            var clipComponent = clip.IsCustomNamed
                ? FileNameSanitizer.ToSafeComponent(clip.DisplayName, $"Clip-{clip.Ordinal}")
                : clip.Ordinal.ToString("00", CultureInfo.InvariantCulture);
            var stem = $"{session.SafeName}_{clipComponent}";
            var candidate = $"{stem}_{date}.mp3";
            if (!reservedFiles.Add(candidate))
            {
                var suffix = 2;
                do
                {
                    candidate = $"{stem}_{suffix:00}_{date}.mp3";
                    suffix++;
                }
                while (!reservedFiles.Add(candidate));
            }

            files.Add(new(clip.Id, clip.Ordinal, candidate));
        }

        return new(folderName, files);
    }

    private static string MakeUnique(string baseName, ISet<string> reserved)
    {
        if (reserved.Add(baseName))
        {
            return baseName;
        }

        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{baseName}_{suffix:00}";
            suffix++;
        }
        while (!reserved.Add(candidate));

        return candidate;
    }
}
