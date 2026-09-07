using System.CommandLine;
using Elevate.Cli.Completion;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class CommandTreeTests
{
    [Fact]
    public void EveryTopLevelCommandIsPresent()
    {
        var root = Program.BuildRootCommand();
        root.Subcommands.Select(c => c.Name).Should().Contain(
            ["login", "logout", "accounts", "tenants", "roles", "status", "watch", "activate", "extend", "deactivate", "cancel",
             "profiles", "approvals", "config", "catalogue", "diagnostics", "update", "completion"]);
    }

    [Theory]
    [InlineData("activate \"Global Reader\" --duration 2h --reason ops --tenant contoso")]
    [InlineData("roles --kind azure --scope prod --json")]
    [InlineData("profiles run Morning --wait")]
    [InlineData("tenants manual add contoso --entra \"Global Reader\" --azure /subscriptions/x=Reader")]
    [InlineData("approvals deny abcd1234 --reason no")]
    [InlineData("config set client-id 11111111-2222-3333-4444-555555555555 --yes")]
    [InlineData("--data-dir /tmp/x --device-code status")]
    public void CommandLinesParseWithoutErrors(string line)
    {
        var result = Program.BuildRootCommand().Parse(line);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void UnknownCommandIsAnError()
    {
        Program.BuildRootCommand().Parse("frobnicate").Errors.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("bash", "complete -F _elevate elevate")]
    [InlineData("zsh", "compdef _elevate elevate")]
    [InlineData("fish", "complete -c elevate")]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    public void CompletionScriptsCoverTheTree(string shell, string marker)
    {
        var script = CompletionScripts.Generate(Program.BuildRootCommand(), shell);
        script.Should().Contain(marker);
        script.Should().Contain("tenants manual add").And.Contain(shell == "fish" ? "-l duration" : "--duration").And.Contain("profiles");
    }

    [Fact]
    public void CompletionPathsIncludeGlobalOptionsEverywhere()
    {
        var paths = CompletionScripts.Paths(Program.BuildRootCommand());
        paths.Should().Contain(p => p.Path == "tenants manual add");
        paths.Single(p => p.Path == "profiles run").Words.Should().Contain("--json").And.Contain("--wait").And.Contain("--help");
        paths.Single(p => p.Path == "").Words.Should().Contain("activate").And.Contain("--help").And.NotContain("/h");
    }

    [Fact]
    public void UnknownShellIsUsageError()
    {
        var act = () => CompletionScripts.Generate(Program.BuildRootCommand(), "cmd");
        act.Should().Throw<Cli.Infrastructure.CliException>().Which.ExitCode.Should().Be(Cli.Infrastructure.ExitCodes.Usage);
    }
}
