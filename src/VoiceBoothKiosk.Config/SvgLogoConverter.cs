using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using VoiceBoothKiosk.Infrastructure.Configuration;

namespace VoiceBoothKiosk.Config;

/// <summary>
/// Converts operator-selected SVG artwork into a static PNG before it enters the
/// managed-logo and preset pipelines. The kiosk never directly loads SVG markup.
/// </summary>
internal static partial class SvgLogoConverter
{
    private const int MaximumRasterDimension = 2048;
    private const double DefaultAspectRatio = 3d;

    public static string ConvertToTemporaryPng(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourcePath));
        ValidateSourceFile(fullSourcePath);

        var sanitizedSvgPath = Path.Combine(Path.GetTempPath(), $"VoiceBooth-logo-{Guid.NewGuid():N}.svg");
        var pngPath = Path.Combine(Path.GetTempPath(), $"VoiceBooth-logo-{Guid.NewGuid():N}.png");
        try
        {
            var document = LoadAndValidateSvg(fullSourcePath);
            var aspectRatio = ReadAspectRatio(document.Root);
            document.Save(sanitizedSvgPath, SaveOptions.DisableFormatting);

            var (pixelWidth, pixelHeight) = CalculatePixelSize(aspectRatio);
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = false,
                TextAsGeometry = true,
                EnsureViewboxSize = true,
                EnsureViewboxPosition = true,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight
            };

            using var converter = new ImageSvgConverter(settings);
            if (!converter.Convert(sanitizedSvgPath, pngPath) ||
                !File.Exists(pngPath) ||
                new FileInfo(pngPath).Length == 0)
            {
                throw new InvalidDataException("The SVG could not be rendered as a PNG logo.");
            }

            return pngPath;
        }
        catch
        {
            TryDeleteTemporaryFile(pngPath);
            throw;
        }
        finally
        {
            TryDeleteTemporaryFile(sanitizedSvgPath);
        }
    }

    public static void TryDeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateSourceFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected SVG logo does not exist.", path);
        }

        if (!string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected vector logo must be an SVG file.");
        }

        var length = new FileInfo(path).Length;
        if (length <= 0 || length > ManagedLogoService.MaximumLogoBytes)
        {
            throw new InvalidDataException("The selected SVG is empty or larger than 25 MB.");
        }
    }

    private static XDocument LoadAndValidateSvg(string path)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ManagedLogoService.MaximumLogoBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

        XDocument document;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = XmlReader.Create(stream, readerSettings))
        {
            document = XDocument.Load(reader, LoadOptions.None);
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected file does not contain an SVG root element.");
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                element.Name.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SVG scripts and embedded foreign content are not supported in station logos.");
            }

            foreach (var attribute in element.Attributes())
            {
                // Namespace declarations identify the SVG vocabulary; they are not
                // resource requests even though their values are URI-shaped.
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SVG event handlers are not supported in station logos.");
                }

                ValidateResourceReference(attribute.Name.LocalName, attribute.Value);
            }

            if (element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                ValidateCssValue(element.Value);
            }
        }

        return document;
    }

    private static void ValidateResourceReference(string attributeName, string value)
    {
        if ((attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
             attributeName.Equals("src", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(value) &&
            !value.TrimStart().StartsWith('#'))
        {
            throw new InvalidDataException("SVG logos cannot load linked files or network resources. Embed the artwork directly in the SVG.");
        }

        ValidateCssValue(value);
    }

    private static void ValidateCssValue(string value)
    {
        if (ForbiddenResourceTextRegex().IsMatch(value))
        {
            throw new InvalidDataException("SVG logos cannot contain scripts, imported styles, or external resource addresses.");
        }

        foreach (Match match in CssUrlRegex().Matches(value))
        {
            var target = match.Groups["target"].Value.Trim();
            if (!target.StartsWith('#'))
            {
                throw new InvalidDataException("SVG logos cannot load external CSS resources.");
            }
        }
    }

    private static double ReadAspectRatio(XElement? root)
    {
        if (root is null)
        {
            return DefaultAspectRatio;
        }

        var viewBoxParts = (root.Attribute("viewBox")?.Value ?? string.Empty)
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (viewBoxParts.Length == 4 &&
            TryReadPositiveNumber(viewBoxParts[2], out var viewBoxWidth) &&
            TryReadPositiveNumber(viewBoxParts[3], out var viewBoxHeight))
        {
            return NormalizeAspectRatio(viewBoxWidth / viewBoxHeight);
        }

        if (TryReadPositiveNumber(root.Attribute("width")?.Value, out var width) &&
            TryReadPositiveNumber(root.Attribute("height")?.Value, out var height))
        {
            return NormalizeAspectRatio(width / height);
        }

        return DefaultAspectRatio;
    }

    private static bool TryReadPositiveNumber(string? value, out double result)
    {
        result = 0;
        var match = LeadingNumberRegex().Match(value ?? string.Empty);
        return match.Success &&
               double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
               double.IsFinite(result) &&
               result > 0;
    }

    private static double NormalizeAspectRatio(double value) =>
        double.IsFinite(value) && value > 0
            ? Math.Clamp(value, 1d / MaximumRasterDimension, MaximumRasterDimension)
            : DefaultAspectRatio;

    private static (int Width, int Height) CalculatePixelSize(double aspectRatio)
    {
        if (aspectRatio >= 1)
        {
            return (MaximumRasterDimension, Math.Max(1, (int)Math.Round(MaximumRasterDimension / aspectRatio)));
        }

        return (Math.Max(1, (int)Math.Round(MaximumRasterDimension * aspectRatio)), MaximumRasterDimension);
    }

    [GeneratedRegex(@"(?:javascript|vbscript|file|https?|ftp)\s*:|@import", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenResourceTextRegex();

    [GeneratedRegex(@"url\(\s*['""]?(?<target>[^'""\)]+)['""]?\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CssUrlRegex();

    [GeneratedRegex(@"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumberRegex();
}
