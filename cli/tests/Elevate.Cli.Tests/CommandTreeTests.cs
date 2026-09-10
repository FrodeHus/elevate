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
            ["login", "logout", "accounts", "tenants", "roles", "status", "watch", "activate", "extend", "deactivate", "cancel", "run",
             "profiles", "approvals", "packages", "config", "consent", "catalogue", "diagnostics", "update", "completion", "init"]);
    }

    [Fact]
    public void RunSeparatesWhatToActivateFromTheCommand()
    {
        var root = Program.BuildRootCommand();
        var run = root.Subcommands.Single(c => c.Name == "run");
        var roles = (Option<string[]>)run.Options.Single(o => o.Name == "--role");
        var command = (Argument<string[]>)run.Arguments.Single(a => a.Name == "command");

        var parse = root.Parse("run --role Reader --role Owner --deactivate-after -- az group list --output table");
        parse.Errors.Should().BeEmpty();
        parse.GetValue(roles).Should().Equal("Reader", "Owner");
        parse.GetValue(command).Should().Equal("az", "group", "list", "--output", "table");
        Program.IsRun(parse).Should().BeTrue();

        // Without the separator the first unknown word starts the command.
        parse = root.Parse("run -p Morning --settle 2m --timeout 1h terraform apply");
        parse.Errors.Should().BeEmpty();
        parse.GetValue(command).Should().Equal("terraform", "apply");

        Program.IsRun(root.Parse("profiles run Morning")).Should().BeFalse("only the top-level run waits for a child process");
        root.Parse("init zsh").Errors.Should().BeEmpty();
        root.Parse("config set token-hint off --account alex").Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("activate \"Global Reader\" --duration 2h --reason ops --tenant contoso")]
    [InlineData("roles --kind azure --scope prod --json")]
    [InlineData("profiles run Morning --wait")]
    [InlineData("tenants manual add contoso --entra \"Global Reader\" --azure /subscriptions/x=Reader")]
    [InlineData("approvals deny abcd1234 --reason no")]
    [InlineData("config set client-id 11111111-2222-3333-4444-555555555555 --yes")]
    [InlineData("config set client-id shared --yes")]
    [InlineData("consent --tenant contoso --json")]
    [InlineData("consent --open")]
    [InlineData("--data-dir /tmp/x --device-code status")]
    [InlineData("packages list --tenant contoso --json")]
    [InlineData("packages requests --all")]
    [InlineData("packages assigned -a alex")]
    [InlineData("packages request \"Azure Sandbox\" --justification \"INC-4412\" --policy Engineers")]
    [InlineData("packages cancel 0123abcd 4567ef01")]
    public void CommandLinesParseWithoutErrors(string line)
    {
        var result = Program.BuildRootCommand().Parse(line);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void PackagesSubcommandsArePresent()
    {
        var packages = Program.BuildRootCommand().Subcommands.Single(c => c.Name == "packages");
        packages.Subcommands.Select(c => c.Name).Should().BeEquivalentTo(["list", "requests", "assigned", "request", "cancel"]);
        Program.BuildRootCommand().Parse("packages request").Errors.Should().NotBeEmpty("the package argument is required");
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
        paths.Single(p => p.Path == "run").Words.Should().Contain("--deactivate-after").And.Contain("--settle").And.Contain("--profile");
        paths.Single(p => p.Path == "").Words.Should().Contain("activate").And.Contain("--help").And.NotContain("/h");
    }

    [Fact]
    public void UnknownShellIsUsageError()
    {
        var act = () => CompletionScripts.Generate(Program.BuildRootCommand(), "cmd");
        act.Should().Throw<Cli.Infrastructure.CliException>().Which.ExitCode.Should().Be(Cli.Infrastructure.ExitCodes.Usage);
    }
}
