using System.CommandLine;
using Elevate.Audit;
using FluentAssertions;

namespace Elevate.Audit.Tests;

[Collection("console")]
public class CommandTreeTests
{
    [Fact]
    public void Root_HasVersionAndUpdateSubcommands()
    {
        var root = Program.BuildRootCommand();

        root.Subcommands.Select(c => c.Name).Should().Contain(["version", "update"]);
    }

    [Fact]
    public async Task Version_PrintsTheToolNameAndVersion()
    {
        var stdout = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var code = await Program.BuildRootCommand().Parse(["version"]).InvokeAsync();
            code.Should().Be(0);
        }
        finally
        {
            Console.SetOut(previous);
        }

        stdout.ToString().Should().StartWith("elevate-audit ");
    }

    [Fact]
    public void FormatUnexpectedError_CollapsesAMultiLineMessageToOneLine()
    {
        var message = Program.FormatUnexpectedError(new InvalidOperationException("line one\nline two\r\nline three"));

        message.Should().Be("Unexpected error: line one line two line three");
    }
}
