using System.CommandLine;
using Elevate.Audit.Commands;
using Elevate.Audit.Infrastructure;
using Elevate.Core.Models;

namespace Elevate.Audit;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        TryUseUtf8();
        var parse = BuildRootCommand().Parse(args);
        var configuration = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
        try
        {
            return await parse.InvokeAsync(configuration).ConfigureAwait(false);
        }
        catch (AuditException e)
        {
            Console.Error.WriteLine(e.Message);
            return e.ExitCode;
        }
        catch (PimException e)
        {
            Console.Error.WriteLine(e.UserMessage);
            return ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Interrupted;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(FormatUnexpectedError(e));
            return ExitCodes.Failure;
        }
    }

    /// <summary>Keeps the catch-all's report to one line even when the exception's own message spans several.</summary>
    internal static string FormatUnexpectedError(Exception e) => $"Unexpected error: {e.Message.ReplaceLineEndings(" ")}";

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

    /// <summary>The whole command tree; shared with the tests.</summary>
    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM.");
        root.Subcommands.Add(VersionCommand.Create());
        root.Subcommands.Add(UpdateCommand.Create());
        ScanCommand.AddTo(root);
        return root;
    }
}
