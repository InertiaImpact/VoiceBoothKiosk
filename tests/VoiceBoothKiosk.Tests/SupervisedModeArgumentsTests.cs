using VoiceBoothKiosk.Supervision;

namespace VoiceBoothKiosk.Tests;

public sealed class SupervisedModeArgumentsTests
{
    [Theory]
    [InlineData(new[] { "--supervised-kiosk" }, true)]
    [InlineData(new[] { "--SUPERVISED-KIOSK", "--windowed" }, true)]
    [InlineData(new[] { "--windowed" }, false)]
    [InlineData(new[] { "--supervised-kiosk", "--supervised-watchdog-child" }, false)]
    public void Supervisor_is_used_only_for_the_outer_supervised_process(string[] arguments, bool expected)
    {
        Assert.Equal(expected, SupervisedModeArguments.ShouldRunSupervisor(arguments));
    }

    [Fact]
    public void Child_arguments_preserve_public_switches_and_add_one_internal_marker()
    {
        var result = SupervisedModeArguments.BuildChildArguments(
            ["--supervised-kiosk", "--windowed", "--supervised-watchdog-child"]);

        Assert.Contains("--supervised-kiosk", result);
        Assert.Contains("--windowed", result);
        Assert.Single(result, argument =>
            string.Equals(argument, "--supervised-watchdog-child", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData("", 120)]
    [InlineData("not-a-number", 120)]
    [InlineData("0", 120)]
    [InlineData("2.5", 2.5)]
    [InlineData("3601", 120)]
    public void Check_interval_defaults_to_two_minutes_and_allows_bounded_test_overrides(
        string? configuredSeconds,
        double expectedSeconds)
    {
        var interval = SupervisedModeArguments.ResolveCheckInterval(configuredSeconds);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }
}
