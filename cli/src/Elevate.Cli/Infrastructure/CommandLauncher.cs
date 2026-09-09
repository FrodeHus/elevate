using System.Diagnostics;

namespace Elevate.Cli.Infrastructure;

/// <summary>
/// Starts the command <c>elevate run</c> was given with the terminal's own stdin, stdout and stderr,
/// so an interactive <c>az login</c> or a piped <c>kubectl get -o json</c> behave as if typed directly.
/// The executable is looked up the way a shell does it, since <c>CreateProcess</c> ignores
/// <c>PATHEXT</c> and would not find <c>az.cmd</c> behind <c>az</c>.
/// </summary>
public static class CommandLauncher
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    /// <summary>
    /// The full path of <paramref name="command"/>, or null. A name with a directory part is tried
    /// as given; a bare name is searched for on <paramref name="path"/>. Every candidate is also
    /// tried with each <paramref name="pathExt"/> extension (Windows: the environment's PATHEXT).
    /// </summary>
    public static string? Resolve(string command, string? path = null, string? pathExt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        path ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        pathExt ??= OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultPathExt : string.Empty;
        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IEnumerable<string> Candidates(string basePath)
        {
            yield return basePath;
            foreach (var extension in extensions)
            {
                if (!basePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    yield return basePath + extension;
                }
            }
        }

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return Candidates(command).FirstOrDefault(File.Exists);
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var found = Candidates(Path.Combine(directory.Trim('"'), command)).FirstOrDefault(File.Exists);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the command to completion and returns its exit code. Ctrl+C reaches the child through the
    /// shared console, so this waits for it to finish rather than watching a cancellation token.
    /// </summary>
    public static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new CliException($"Could not start {executable}.", ExitCodes.Failure);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return process.ExitCode;
    }
}
