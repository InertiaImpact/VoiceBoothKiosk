namespace VoiceBoothKiosk.Domain;

public enum SessionState
{
    Creating,
    Active,
    Exporting,
    Completed,
    Recoverable,
    Quarantined
}

public enum ClipState
{
    Empty,
    Stopped,
    Recording,
    Playing,
    Paused,
    Error,
    Deleted
}

public enum SegmentState
{
    Recording,
    Finalized,
    Recoverable,
    Failed
}

public enum AudioDeviceFlow
{
    Input,
    Output
}

public enum ClipRailPosition
{
    Left,
    Right
}

public enum ControlSize
{
    Compact,
    Standard,
    Large
}

public enum ExportState
{
    None,
    Validating,
    Encoding,
    Committing,
    Completed,
    Failed,
    Canceled
}
