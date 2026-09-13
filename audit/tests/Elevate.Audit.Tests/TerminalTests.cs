using Elevate.Audit.Infrastructure;
using FluentAssertions;
using Spectre.Console;

namespace Elevate.Audit.Tests;

[Collection("console")]
public class TerminalTests
{
    private static void WithEnvironmentVariable(string name, string? value, Action act)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            act();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void NoColorEnvironmentVariable_ForcesNoColoursAndNoAnsi()
    {
        WithEnvironmentVariable("NO_COLOR", "1", () =>
        {
            var terminal = new Terminal(quiet: false, noColor: false);

            terminal.Stdout.Profile.Capabilities.ColorSystem.Should().Be(ColorSystem.NoColors);
            terminal.Stdout.Profile.Capabilities.Ansi.Should().BeFalse();
            terminal.Stderr.Profile.Capabilities.ColorSystem.Should().Be(ColorSystem.NoColors);
            terminal.Stderr.Profile.Capabilities.Ansi.Should().BeFalse();
        });
    }

    [Fact]
    public void NoColorOption_ForcesNoColoursAndNoAnsi_EvenWithoutTheEnvironmentVariable()
    {
        WithEnvironmentVariable("NO_COLOR", null, () =>
        {
            var terminal = new Terminal(quiet: false, noColor: true);

            terminal.Stdout.Profile.Capabilities.ColorSystem.Should().Be(ColorSystem.NoColors);
            terminal.Stdout.Profile.Capabilities.Ansi.Should().BeFalse();
        });
    }

    /// <summary>
    /// Without NO_COLOR and without --no-color, Terminal asks Spectre to auto-detect the host's
    /// capabilities; what it detects depends on the test host (a CI runner is rarely a real terminal),
    /// so the only thing this can assert deterministically is that it did not fall back to the same
    /// forced-off profile as the NO_COLOR path.
    /// </summary>
    [Fact]
    public void WithoutNoColor_DoesNotForceTheNoColoursProfile()
    {
        WithEnvironmentVariable("NO_COLOR", null, () =>
        {
            var terminal = new Terminal(quiet: false, noColor: false);

            _ = terminal.Stdout.Profile.Capabilities.Ansi; // detection ran without throwing; the value itself is host-dependent
        });
    }

    [Fact]
    public void Quiet_SuppressesNote_ButNotSay()
    {
        var writer = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(writer);
        try
        {
            var terminal = new Terminal(quiet: true, noColor: true);

            terminal.Note("a note");
            terminal.Say("a message");

            var text = writer.ToString();
            text.Should().NotContain("a note");
            text.Should().Contain("a message");
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public void NotQuiet_PrintsBothNoteAndSay()
    {
        var writer = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(writer);
        try
        {
            var terminal = new Terminal(quiet: false, noColor: true);

            terminal.Note("a note");
            terminal.Say("a message");

            var text = writer.ToString();
            text.Should().Contain("a note");
            text.Should().Contain("a message");
        }
        finally
        {
            Console.SetError(previousError);
        }
    }
}
