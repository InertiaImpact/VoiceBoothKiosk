namespace VoiceBoothKiosk.Audio.Windows;

/// <summary>
/// Retries only Windows sharing and lock violations. Other I/O failures (for example,
/// a disconnected destination or a full disk) are surfaced immediately.
/// </summary>
internal static class TransientFileOperation
{
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(1_600),
        TimeSpan.FromMilliseconds(2_400),
        TimeSpan.FromMilliseconds(3_000),
    ];

    internal static void Execute(
        Action operation,
        CancellationToken cancellationToken = default,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Execute(
            () =>
            {
                operation();
                return true;
            },
            cancellationToken,
            retryDelays);
    }

    internal static T Execute<T>(
        Func<T> operation,
        CancellationToken cancellationToken = default,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var delays = retryDelays ?? DefaultRetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (IOException exception) when (IsSharingOrLockViolation(exception) && attempt < delays.Count)
            {
                var delay = delays[attempt];
                if (delay < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(retryDelays), "Retry delays cannot be negative.");
                }

                Task.Delay(delay, cancellationToken).GetAwaiter().GetResult();
            }
        }
    }

    internal static bool IsSharingOrLockViolation(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
    }
}
