using System.Runtime.InteropServices;

namespace Elevate.Cli.Infrastructure;

/// <summary>
/// Where the CLI keeps its own <c>state.json</c>, <c>settings.json</c> and token cache. Separate
/// from the desktop apps' directory on purpose: each app validates the accounts in its state
/// against its own token cache and signs out the ones it cannot find, so two programs with
/// different caches must not share one state file. <c>--data-dir</c> or <c>ELEVATE_CLI_HOME</c>
/// override the default; <c>elevate profiles import</c> copies profiles over from the app.
/// </summary>
public static class DataDirectory
{
    public const string EnvironmentVariable = "ELEVATE_CLI_HOME";

    public static string Resolve(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }

        return Path.Combine(PlatformBase(), "elevate-cli");
    }

    /// <summary>The desktop app's <c>state.json</c> on this platform, or null where no app exists.</summary>
    public static string? DesktopAppStateFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elevate", "state.json");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Home(), "Library", "Application Support", "Elevate", "state.json");
        }

        return null;
    }

    private static string PlatformBase()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Home(), "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Home(), ".config") : xdg;
    }

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
