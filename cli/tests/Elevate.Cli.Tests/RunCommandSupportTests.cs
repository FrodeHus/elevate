using Elevate.Cli.Completion;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Selection;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>The pieces under <c>elevate run</c> and <c>elevate init</c> that need no session.</summary>
public class RunCommandSupportTests
{
    [Fact]
    public void ResolveFindsABareNameOnThePathWithAndWithoutAnExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "az.cmd"), "@echo off");
            // The Azure CLI ships this extensionless bash script next to az.cmd; with PATHEXT in force it must be skipped.
            File.WriteAllText(Path.Combine(dir, "az"), "#!/bin/bash");
            File.WriteAllText(Path.Combine(dir, "kubectl"), "#!/bin/sh");
            var path = "/nowhere" + Path.PathSeparator + dir;

            // The extension is probed as PATHEXT spells it. Windows ignores the case, but this test also
            // runs on Linux where the file system does not, so PATHEXT here spells it the way the file does.
            CommandLauncher.Resolve("az", path, ".COM;.EXE;.cmd").Should().Be(Path.Combine(dir, "az.cmd"));
            CommandLauncher.Resolve("az.cmd", path, ".COM;.EXE;.cmd").Should().Be(Path.Combine(dir, "az.cmd"));
            CommandLauncher.Resolve("kubectl", path, string.Empty).Should().Be(Path.Combine(dir, "kubectl"));
            CommandLauncher.Resolve("terraform", path, ".COM;.EXE;.cmd").Should().BeNull();
            CommandLauncher.Resolve(Path.Combine(dir, "az"), string.Empty, ".cmd").Should().Be(Path.Combine(dir, "az.cmd"), "a path is tried as given, with the extensions");
            CommandLauncher.Resolve(Path.Combine(dir, "missing"), string.Empty, ".cmd").Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RunPassesTheExitCodeThrough()
    {
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? (CommandLauncher.Resolve("cmd.exe")!, new[] { "/c", "exit 3" })
            : (CommandLauncher.Resolve("sh")!, new[] { "-c", "exit 3" });

        (await CommandLauncher.RunAsync(executable, arguments)).Should().Be(3);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("2m", 120)]
    [InlineData("1m30s", 90)]
    [InlineData("1h", 3600)]
    [InlineData("45", 45)]
    [InlineData("0", 0)]
    public void ShortDurationsCountSeconds(string text, int seconds) =>
        ShortDurationParser.Parse(text).Should().Be(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void ShortDurationRejectsNonsenseAndZeroWhereAsked()
    {
        ShortDurationParser.Parse("soon").Should().BeNull();
        var nonsense = () => ShortDurationParser.Require("soon", "timeout", allowZero: false);
        nonsense.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
        var zero = () => ShortDurationParser.Require("0", "timeout", allowZero: false);
        zero.Should().Throw<CliException>().Which.Message.Should().Contain("longer than zero");
        ShortDurationParser.Require("0", "settle time", allowZero: true).Should().Be(TimeSpan.Zero);
        ShortDurationParser.Label(TimeSpan.FromSeconds(90)).Should().Be("1 min 30 s");
        ShortDurationParser.Label(TimeSpan.FromMinutes(15)).Should().Be("15 min");
        ShortDurationParser.Label(TimeSpan.FromSeconds(30)).Should().Be("30 s");
    }

    [Theory]
    [InlineData("bash", "PIPESTATUS", "elevate_hint_wrap")]
    [InlineData("zsh", "pipestatus[1]", "elevate_hint_wrap")]
    [InlineData("fish", "2>| tee", "elevate_hint_wrap")]
    [InlineData("pwsh", "Invoke-ElevateHintRun", "Register-ElevateHint")]
    [InlineData("powershell", "LASTEXITCODE", "Register-ElevateHint")]
    public void ShellHooksWrapTheUsualToolsAndPointAtRun(string shell, string marker, string registrar)
    {
        var script = ShellHooks.Generate(shell);
        script.Should().Contain(marker).And.Contain(registrar).And.Contain("elevate run --role <role> --").And.Contain("AuthorizationFailed");
        foreach (var command in ShellHooks.DefaultCommands)
        {
            script.Should().Contain(command);
        }
    }

    [Fact]
    public void UnknownShellIsAUsageError()
    {
        var act = () => ShellHooks.Generate("cmd");
        act.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
    }
}
