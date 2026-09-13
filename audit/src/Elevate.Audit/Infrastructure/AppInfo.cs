using System.Reflection;
using System.Runtime.InteropServices;

namespace Elevate.Audit.Infrastructure;

public static class AppInfo
{
    public const string Name = "elevate-audit";

    /// <summary>The informational version stamped by the build (`-p:Version=x.y.z`), "0.0.0" in a dev build.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Runtime => $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}";
}
