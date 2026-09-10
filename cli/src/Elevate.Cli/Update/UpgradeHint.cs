namespace Elevate.Cli.Update;

/// <summary>
/// The one-line "how to upgrade" note under an available update. The CLI is installed four ways
/// (inside Elevate.app by the pkg or the cask, in the cli folder under Elevate.exe by the MSI, the Homebrew
/// formula, a bare archive or winget) and the right instruction depends on which one this
/// executable came from.
/// </summary>
internal static class UpgradeHint
{
    public static string For(string? executablePath, bool isWindows, bool isMacOS)
    {
        if (isMacOS && executablePath is not null
            && executablePath.Contains(".app/Contents/Helpers/", StringComparison.Ordinal))
        {
            return "Installed with Elevate.app: upgrade the app (brew upgrade --cask frodehus/elevate/elevate, or the newer Elevate pkg).";
        }

        if (isWindows)
        {
            var directory = executablePath is null ? null : Path.GetDirectoryName(executablePath);
            // The MSI installs the CLI in the `cli` subfolder of the app's folder (elevate.exe and
            // Elevate.exe cannot share a directory on a case-insensitive file system).
            var parent = directory is null ? null : Path.GetDirectoryName(directory);
            if (parent is not null && File.Exists(Path.Combine(parent, "Elevate.exe")))
            {
                return "Installed by the Elevate MSI: run the newer Elevate-<version>-x64.msi or -arm64.msi.";
            }
            return "winget: winget upgrade Reothor.Elevate.CLI · or download the newer elevate-cli zip.";
        }

        if (isMacOS)
        {
            return "Homebrew: brew upgrade frodehus/elevate/elevate-cli (deprecated; the elevate cask now installs the CLI) · or download the newer elevate-cli archive.";
        }

        return "Homebrew: brew upgrade frodehus/elevate/elevate-cli · or download the newer elevate-cli archive.";
    }
}
