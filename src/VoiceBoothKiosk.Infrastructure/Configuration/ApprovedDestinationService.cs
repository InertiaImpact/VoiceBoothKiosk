using VoiceBoothKiosk.Domain;
using VoiceBoothKiosk.Infrastructure.Storage;

namespace VoiceBoothKiosk.Infrastructure.Configuration;

public sealed class ApprovedDestinationService
{
    private readonly KioskSettings _settings;

    public ApprovedDestinationService(KioskSettings settings) =>
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public IReadOnlyList<DestinationPreset> GetEnabled() =>
        _settings.Destinations.Where(destination => destination.IsEnabled).ToArray();

    public DestinationPreset Resolve(string destinationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        return _settings.Destinations.FirstOrDefault(candidate => candidate.IsEnabled
            && string.Equals(candidate.Id, destinationId, StringComparison.OrdinalIgnoreCase))
            ?? throw new UnauthorizedAccessException("The selected folder is not an enabled administrator-approved destination.");
    }

    public DestinationInspection Inspect(string destinationId, bool testWrite = true)
    {
        var destination = Resolve(destinationId);
        return DestinationInspector.Inspect(destination.RootPath, testWrite);
    }

    public string ResolveApprovedChildPath(string destinationId, string childPath)
    {
        var root = DestinationInspector.NormalizeApprovedRoot(Resolve(destinationId).RootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, childPath));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("The requested path escapes its approved destination root.");
        return candidate;
    }
}
