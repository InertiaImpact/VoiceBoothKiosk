using VoiceBoothKiosk.Audio.Windows;

namespace VoiceBoothKiosk.Tests;

public sealed class TransientFileOperationTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void Sharing_and_lock_violations_are_retried(int win32Error)
    {
        var attempts = 0;

        TransientFileOperation.Execute(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw CreateIOException(win32Error);
                }
            },
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Retry_budget_is_bounded_and_preserves_the_last_lock_failure()
    {
        var attempts = 0;

        var exception = Assert.Throws<IOException>(() => TransientFileOperation.Execute(
            () =>
            {
                attempts++;
                throw CreateIOException(32);
            },
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]));

        Assert.Equal(4, attempts);
        Assert.Equal(32, exception.HResult & 0xFFFF);
    }

    [Fact]
    public void Unrelated_io_failures_are_not_retried()
    {
        var attempts = 0;

        Assert.Throws<IOException>(() => TransientFileOperation.Execute(
            () =>
            {
                attempts++;
                throw CreateIOException(112); // ERROR_DISK_FULL
            },
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero]));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Cancellation_stops_retries()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => TransientFileOperation.Execute(
            () =>
            {
                attempts++;
                cancellation.Cancel();
                throw CreateIOException(32);
            },
            cancellation.Token,
            [TimeSpan.FromSeconds(1)]));

        Assert.Equal(1, attempts);
    }

    private static IOException CreateIOException(int win32Error) =>
        new("Simulated Windows file-lock failure.", unchecked((int)(0x80070000u | (uint)win32Error)));
}
