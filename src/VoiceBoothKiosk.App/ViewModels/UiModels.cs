namespace VoiceBoothKiosk.App.ViewModels;

public sealed class ClipItemViewModel : ObservableObject
{
    private string _name = string.Empty;
    private TimeSpan _duration;
    private string _status = "Ready";
    private bool _isEmpty = true;

    public required Guid Id { get; init; }
    public required int Ordinal { get; init; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (SetProperty(ref _duration, value))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string DurationText => Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }
}

public sealed record DeviceOptionViewModel(string Id, string Name, bool IsDefault = false, bool IsAvailable = true)
{
    public string DisplayName => !IsAvailable
        ? $"{Name} (Unavailable)"
        : IsDefault ? $"{Name} (Default)" : Name;
}

public sealed record DestinationOptionViewModel(string Id, string Name, string Path)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Path) ? Name : $"{Name}  -  {Path}";
}
