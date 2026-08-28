using VoiceBoothKiosk.Domain;

namespace VoiceBoothKiosk.Tests;

public sealed class RecorderRuntimeContextTests
{
    [Fact]
    public void PublicLaunchMode_PreservesSupervisedModeWithoutInternalChildSwitch()
    {
        var result = RecorderRuntimeContext.GetPublicLaunchMode(
            ["--supervised-kiosk", "--supervised-watchdog-child"]);

        Assert.Equal("--supervised-kiosk", result.Arguments);
        Assert.Equal("Supervised full-screen", result.Mode);
    }

    [Theory]
    [InlineData("--windowed", "--windowed", "Windowed test")]
    [InlineData("--unrecognized", "", "Standard")]
    public void PublicLaunchMode_UsesOnlySupportedPublicMode(
        string supplied,
        string expectedArguments,
        string expectedMode)
    {
        var result = RecorderRuntimeContext.GetPublicLaunchMode([supplied]);

        Assert.Equal(expectedArguments, result.Arguments);
        Assert.Equal(expectedMode, result.Mode);
    }

    [Fact]
    public void EnvironmentValue_RoundTripsDiagnosticSnapshot()
    {
        var originalEnvironmentValue = Environment.GetEnvironmentVariable(RecorderRuntimeContext.EnvironmentVariableName);
        try
        {
            var expected = new RecorderRuntimeContext
            {
                CapturedAtUtc = new DateTimeOffset(2026, 7, 21, 14, 30, 0, TimeSpan.Zero),
                IsReady = false,
                OverallStatus = "No available microphone",
                InputStatus = "Unavailable",
                StorageStatus = "Ready",
                EncoderStatus = "Ready",
                KeepAwakeStatus = "Active — preventing display and system idle sleep",
                RecorderExecutablePath = @"C:\Voice Booth\VoiceBoothKiosk.exe",
                RecorderArguments = "--supervised-kiosk",
                LaunchMode = "Supervised full-screen"
            };
            Environment.SetEnvironmentVariable(
                RecorderRuntimeContext.EnvironmentVariableName,
                expected.ToEnvironmentValue());

            var actual = RecorderRuntimeContext.FromEnvironment();

            Assert.Equal(expected, actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RecorderRuntimeContext.EnvironmentVariableName, originalEnvironmentValue);
        }
    }
}
