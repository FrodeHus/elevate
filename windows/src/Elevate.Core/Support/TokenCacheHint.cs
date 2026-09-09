using Elevate.Core.Coordination;
using Elevate.Core.Models;

namespace Elevate.Core.Support;

/// <summary>
/// After an Azure resource role or a group membership becomes active, the Azure CLI, Azure
/// PowerShell and kubelogin keep using the token they cached before the activation, which lacks
/// the new assignment or group claim, so the next command is still refused. This is the most
/// common "I activated but I am still denied" report. Lives in Core so the apps and the CLI word
/// the hint the same way and the check is testable without either.
/// </summary>
public static class TokenCacheHint
{
    /// <summary>Refreshes the Azure CLI's cached ARM token without signing out; the safe alternative to <c>az account clear</c>.</summary>
    public const string AzureCliCommand = "az account get-access-token --force-refresh";

    /// <summary>Drops kubelogin's cached AKS tokens; the next kubectl call gets a fresh one.</summary>
    public const string KubeloginCommand = "kubelogin remove-tokens";

    /// <summary>The line to put in front of the user; the commands follow in <see cref="Advice"/>.</summary>
    public static string Message(string account) =>
        $"Tokens the Azure CLI, Azure PowerShell and kubelogin cached for {account} may predate this activation and lack the new assignment.";

    /// <summary>What to run before retrying, naming the exact commands.</summary>
    public static string Advice =>
        $"Refresh with '{AzureCliCommand}' (AKS: '{KubeloginCommand}'); Azure PowerShell needs Connect-AzAccount again.";

    /// <summary>
    /// Ids of the accounts whose cached tokens the outcomes make stale: those with an Azure resource
    /// role or a group membership that just became active. Entra directory roles are read from Graph
    /// with the app's own token and do not qualify. Distinct, in outcome order.
    /// </summary>
    public static IReadOnlyList<string> AffectedAccounts(IEnumerable<ActivationOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var ids = new List<string>();
        foreach (var outcome in outcomes)
        {
            if (outcome.Result is ActivationResult.Activated && Qualifies(outcome.RoleKey) && !ids.Contains(outcome.RoleKey.IdentityId))
            {
                ids.Add(outcome.RoleKey.IdentityId);
            }
        }

        return ids;
    }

    /// <summary>Whether a role of this key, once active, is one the cached tokens would not know about.</summary>
    public static bool Qualifies(RoleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Scope.Kind is RoleScopeKind.AzureResource or RoleScopeKind.Group;
    }
}
