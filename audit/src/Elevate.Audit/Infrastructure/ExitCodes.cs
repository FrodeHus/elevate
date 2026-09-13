namespace Elevate.Audit.Infrastructure;

/// <summary>Process exit codes. Pipelines branch on these; keep them stable.</summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The scan could not complete (sign-in, network, a required source refused).</summary>
    public const int Failure = 1;

    /// <summary>The scan completed and found at least one High finding.</summary>
    public const int HighFindings = 2;

    /// <summary>Interrupted with Ctrl+C.</summary>
    public const int Interrupted = 130;
}

/// <summary>An error the tool reports on stderr as one line and turns into an exit code; never a stack trace.</summary>
public sealed class AuditException(string message, int exitCode = ExitCodes.Failure) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
