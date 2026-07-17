namespace VoiceBoothKiosk.Domain;

/// <summary>Raised when a requested operation violates the recording domain's state rules.</summary>
public sealed class DomainException : InvalidOperationException
{
    public DomainException(string message) : base(message)
    {
    }
}
