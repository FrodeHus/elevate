using Elevate.Cli.Update;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class UpgradeHintTests
{
    [Fact]
    public void Helper_inside_the_mac_app_bundle_points_at_the_app()
    {
        UpgradeHint.For("/Applications/Elevate.app/Contents/Helpers/elevate", isWindows: false, isMacOS: true)
            .Should().Be("Installed with Elevate.app: upgrade the app (brew upgrade --cask frodehus/elevate/elevate, or the newer Elevate pkg).");
    }

    [Fact]
    public void Standalone_mac_binary_names_the_formula_and_the_archive()
    {
        UpgradeHint.For("/opt/homebrew/bin/elevate", isWindows: false, isMacOS: true)
            .Should().Be("Homebrew: brew upgrade frodehus/elevate/elevate-cli (deprecated; the elevate cask now installs the CLI) · or download the newer elevate-cli archive.");
    }

    [Fact]
    public void Exe_next_to_the_windows_app_points_at_the_msi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "cli"));
        File.WriteAllText(Path.Combine(dir, "Elevate.exe"), "");
        try
        {
            UpgradeHint.For(Path.Combine(dir, "cli", "elevate.exe"), isWindows: true, isMacOS: false)
                .Should().Be("Installed by the Elevate MSI: run the newer Elevate-<version>-x64.msi or -arm64.msi.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Standalone_windows_exe_names_winget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            UpgradeHint.For(Path.Combine(dir, "elevate.exe"), isWindows: true, isMacOS: false)
                .Should().Be("winget: winget upgrade Reothor.Elevate.CLI · or download the newer elevate-cli zip.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Linux_names_the_formula_and_the_archive()
    {
        UpgradeHint.For("/usr/local/bin/elevate", isWindows: false, isMacOS: false)
            .Should().Be("Homebrew: brew upgrade frodehus/elevate/elevate-cli · or download the newer elevate-cli archive.");
    }

    [Fact]
    public void Unknown_path_falls_back_to_the_archive()
    {
        UpgradeHint.For(null, isWindows: false, isMacOS: false)
            .Should().Be("Homebrew: brew upgrade frodehus/elevate/elevate-cli · or download the newer elevate-cli archive.");
    }
}
