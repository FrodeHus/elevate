namespace Elevate.Cli.Infrastructure;

/// <summary>Process exit codes. Scripts branch on these; keep them stable.</summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The operation failed (network, service refusal, unreadable state).</summary>
    public const int Failure = 1;

    /// <summary>Bad arguments, or a value the command needed was neither given nor remembered.</summary>
    public const int Usage = 2;

    /// <summary>No account, or the account needs an interactive sign-in this run could not do.</summary>
    public const int SignInRequired = 3;

    /// <summary>A role, tenant, account, profile or request did not match, or matched more than one.</summary>
    public const int NotFound = 4;

    /// <summary>Some, not all, of the requested activations or decisions went through.</summary>
    public const int Partial = 5;

    /// <summary><c>elevate run</c> could not find the command to run; the shells' own code for it.</summary>
    public const int CommandNotFound = 127;

    /// <summary>Interrupted with Ctrl+C.</summary>
    public const int Interrupted = 130;
}

/// <summary>An error the CLI reports on stderr and turns into an exit code; never a stack trace.</summary>
public sealed class CliException(string message, int exitCode = ExitCodes.Failure) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
