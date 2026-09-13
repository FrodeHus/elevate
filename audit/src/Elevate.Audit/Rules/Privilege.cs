using System.Text.Json;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

/// <summary>Which Azure roles count as privileged: the bundled list by name, plus custom roles that can do anything or hand out roles.</summary>
public static class Privilege
{
    internal const string ResourceName = "Elevate.Audit.Resources.AzurePrivilegedRoles.json";

    internal sealed record AzureRoleEntry(string Name, string Severity);

    private static readonly Lazy<IReadOnlyDictionary<string, Severity>> AzureList = new(Load);

    public static Severity? AzureSeverityFor(AzureRoleDefinitionRecord role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (AzureList.Value.TryGetValue(role.DisplayName, out var severity))
        {
            return severity;
        }

        if (!string.Equals(role.Type, "BuiltInRole", StringComparison.OrdinalIgnoreCase)
            && role.Actions.Any(a => a == "*" || a.StartsWith("Microsoft.Authorization/", StringComparison.OrdinalIgnoreCase)
                && (a.EndsWith("/write", StringComparison.OrdinalIgnoreCase) || a.EndsWith("/*", StringComparison.OrdinalIgnoreCase))))
        {
            return Severity.Medium;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, Severity> Load()
    {
        using var stream = typeof(Privilege).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("AzurePrivilegedRoles.json missing from the bundle");
        var entries = JsonSerializer.Deserialize<List<AzureRoleEntry>>(stream, AuditJson.Options) ?? [];
        return entries.ToDictionary(e => e.Name, e => Severities.Parse(e.Severity), StringComparer.OrdinalIgnoreCase);
    }
}
