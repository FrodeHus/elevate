namespace Elevate.Audit.Auth;

public enum Resource { Graph, Arm }

/// <summary>The two Microsoft public clients the auditor signs in with, and what it asks each for.</summary>
public static class ClientIds
{
    /// <summary>"Microsoft Graph Command Line Tools": first-party, multi-tenant, dynamic consent, loopback and device-code redirects.</summary>
    public const string GraphDefault = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public const string GraphDefaultDisplayName = "Microsoft Graph Command Line Tools";

    /// <summary>The Azure CLI's client, pre-consented for Azure Resource Manager in every tenant.</summary>
    public const string AzureCli = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    public const string GraphDefaultScope = "https://graph.microsoft.com/.default";
    public const string ArmDefaultScope = "https://management.azure.com/.default";

    /// <summary>Read-only, in the order shown to the consenting administrator.</summary>
    public static IReadOnlyList<string> GraphReadScopes { get; } =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/RoleManagement.Read.Directory",
        "https://graph.microsoft.com/PrivilegedAssignmentSchedule.Read.AzureADGroup",
        "https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup",
        "https://graph.microsoft.com/GroupMember.Read.All",
        "https://graph.microsoft.com/User.ReadBasic.All",
    ];

    /// <summary>The bare scope names, for docs and the report appendix.</summary>
    public static IReadOnlyList<string> GraphReadScopeNames { get; } = GraphReadScopes.Select(s => s["https://graph.microsoft.com/".Length..]).ToList();

    /// <summary>
    /// Which client a request belongs to and what to ask MSAL for. Graph with the default client asks for
    /// the named read scopes every time so consent happens once, up front; a custom client and ARM ask for
    /// <c>.default</c>, since what they may do is whatever was consented to them.
    /// </summary>
    public static (Resource Resource, IReadOnlyList<string> Scopes) ScopesFor(IReadOnlyList<string> requested, bool customGraphClient)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var isArm = requested.Any(s => s.StartsWith("https://management.azure.com/", StringComparison.OrdinalIgnoreCase));
        if (isArm)
        {
            return (Resource.Arm, [ArmDefaultScope]);
        }

        return (Resource.Graph, customGraphClient ? [GraphDefaultScope] : GraphReadScopes);
    }

    public static bool IsValidClientId(string? value) => Guid.TryParse((value ?? string.Empty).Trim(), out var guid) && guid != Guid.Empty;
}
