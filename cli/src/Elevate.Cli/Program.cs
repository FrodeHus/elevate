using System.CommandLine;
using System.CommandLine.Parsing;
using Elevate.Cli.Commands;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Models;

namespace Elevate.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        TryUseUtf8();
        var root = BuildRootCommand();
        var parse = root.Parse(args);
        var configuration = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
        if (IsRun(parse))
        {
            // Ctrl+C goes to the command `run` started as much as to elevate. The default two-second
            // grace would end elevate while the command is still cleaning up; wait for it instead.
            configuration.ProcessTerminationTimeout = TimeSpan.FromDays(1);
        }

        try
        {
            // Exceptions come back here for one plain line on stderr and an exit code, not a stack trace.
            return await parse.InvokeAsync(configuration).ConfigureAwait(false);
        }
        catch (CliException e)
        {
            Console.Error.WriteLine(e.Message);
            return e.ExitCode;
        }
        catch (PimException e)
        {
            Console.Error.WriteLine(e.UserMessage);
            return e.Kind is PimErrorKind.InteractionRequired or PimErrorKind.SignInDeclined ? ExitCodes.SignInRequired : ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Interrupted;
        }
    }

    /// <summary>Box-drawing and ellipses need UTF-8; the classic Windows console defaults to a code page.</summary>
    private static void TryUseUtf8()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception e) when (e is IOException or System.Security.SecurityException)
        {
            // Redirected or restricted output; leave the encoding alone.
        }
    }

    /// <summary>The whole command tree; shared with the completion generator and the tests.</summary>
    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Just-in-time Entra, Azure and PIM for Groups activation from the terminal.");
        root.Options.Add(CommandContext.JsonOption);
        root.Options.Add(CommandContext.QuietOption);
        root.Options.Add(CommandContext.NoColorOption);
        root.Options.Add(CommandContext.DeviceCodeOption);
        root.Options.Add(CommandContext.DataDirOption);

        root.Subcommands.Add(AccountCommands.Login());
        root.Subcommands.Add(AccountCommands.Logout());
        root.Subcommands.Add(AccountCommands.Accounts());
        root.Subcommands.Add(TenantCommands.Tenants());
        root.Subcommands.Add(RoleCommands.Roles());
        root.Subcommands.Add(RoleCommands.Status());
        root.Subcommands.Add(RoleCommands.Watch());
        root.Subcommands.Add(ActivationCommands.Activate());
        root.Subcommands.Add(ActivationCommands.Extend());
        root.Subcommands.Add(ActivationCommands.Deactivate());
        root.Subcommands.Add(ActivationCommands.Cancel());
        root.Subcommands.Add(RunCommands.Run());
        root.Subcommands.Add(ProfileCommands.Profiles());
        root.Subcommands.Add(ApprovalCommands.Approvals());
        root.Subcommands.Add(PackageCommands.Packages());
        root.Subcommands.Add(ConfigCommands.Config());
        root.Subcommands.Add(MiscCommands.Catalogue());
        root.Subcommands.Add(MiscCommands.Diagnostics());
        root.Subcommands.Add(MiscCommands.Update());
        root.Subcommands.Add(MiscCommands.Completion());
        root.Subcommands.Add(MiscCommands.Init());

        // Bare `elevate` shows what is active, the way opening the desktop panel does.
        root.SetAction((parse, ct) => RoleCommands.RunStatusAsync(CommandContext.From(parse), ct));
        return root;
    }

    /// <summary>Whether the invoked command is the top-level <c>run</c> (not <c>profiles run</c>).</summary>
    internal static bool IsRun(ParseResult parse) =>
        parse.CommandResult.Command.Name == "run" && parse.CommandResult.Parent is CommandResult { Command: RootCommand };
}
