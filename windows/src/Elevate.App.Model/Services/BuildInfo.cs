using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Elevate.App.Services;

/// <summary>What the running build is, for Settings and the diagnostics report. Port of the macOS <c>BuildInfo</c>.</summary>
public static class BuildInfo
{
    private static readonly Lazy<(string Version, string Build)> Versions = new(ReadVersions);
    private static readonly Lazy<string> Signing = new(ReadSigning);

    /// <summary>"1.2.3": the informational version without build metadata.</summary>
    public static string Version => Versions.Value.Version;

    /// <summary>The build metadata after the "+", or the assembly's four-part version.</summary>
    public static string Build => Versions.Value.Build;

    /// <summary>"Unsigned", or the subject of the Authenticode certificate on the running executable.</summary>
    public static string SigningDescription => Signing.Value;

    /// <summary>"Windows 11 (10.0.26200)" style text.</summary>
    public static string OsDescription => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    private static (string, string) ReadVersions()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var parts = (informational ?? string.Empty).Split('+', 2);
        var version = parts[0].Length > 0 ? parts[0] : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var build = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : assembly.GetName().Version?.ToString() ?? "0";
        return (version, build);
    }

    private static string ReadSigning()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is null)
            {
                return "Unknown";
            }

            // The signed-file loader is the only one that reads an Authenticode signature off a PE file.
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return $"Signed: {certificate.Subject}";
        }
        catch (Exception)
        {
            return "Unsigned";
        }
    }
}
