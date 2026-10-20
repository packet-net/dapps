using AwesomeAssertions;
using dapps.core.Updater;

namespace dapps.core.tests;

/// <summary>
/// The self-updater is gone, but a dapps-updater timer left over from an older
/// install still runs <c>dapps --apply-update</c> every minute. That must not
/// fall through and boot a second daemon.
/// </summary>
public class DappsCliTests
{
    [Theory]
    [InlineData("--apply-update", 0)]
    [InlineData("--check-update", 2)]
    [InlineData("--rollback", 2)]
    public void Removed_updater_flags_are_handled_without_booting_the_host(string flag, int expectedExit)
    {
        var err = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            DappsCli.TryHandle([flag], out var exit).Should().BeTrue();
            exit.Should().Be(expectedExit);
        }
        finally
        {
            Console.SetError(err);
        }
        captured.ToString().Should().Contain("no longer updates itself").And.Contain("apt");
    }

    [Fact]
    public void Ordinary_arguments_are_left_to_the_host()
    {
        DappsCli.TryHandle([], out _).Should().BeFalse();
        DappsCli.TryHandle(["--urls", "http://localhost:5000"], out _).Should().BeFalse();
    }
}
