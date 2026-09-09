import Foundation

/// After an Azure resource role or a group membership becomes active, the Azure CLI, Azure
/// PowerShell and kubelogin keep using the token they cached before the activation, which lacks
/// the new assignment or group claim, so the next command is still refused. This is the most
/// common "I activated but I am still denied" report. Lives in Core so the app and the CLI word
/// the hint the same way and the check is testable without either.
public enum TokenCacheHint {
    /// Signing in again is the only way to get an Azure CLI token that carries the new assignment:
    /// `az account get-access-token` reuses the cached one and has no flag to force a fresh sign-in.
    public static let azureCliCommand = "az login"

    /// Drops kubelogin's cached AKS tokens; the next kubectl call gets a fresh one.
    public static let kubeloginCommand = "kubelogin remove-tokens"

    /// The line to put in front of the user; the commands follow in `advice`.
    public static func message(account: String) -> String {
        "Azure CLI, Azure PowerShell and kubelogin tokens cached for \(account) predate this activation."
    }

    /// What to run before retrying, naming the exact commands.
    public static var advice: String {
        "Run '\(azureCliCommand)' again (AKS: '\(kubeloginCommand)'); Azure PowerShell needs Connect-AzAccount again."
    }

    /// Ids of the accounts whose cached tokens the outcomes make stale: those with an Azure resource
    /// role or a group membership that just became active. Entra directory roles are read from Graph
    /// with the app's own token and do not qualify. Distinct, in outcome order.
    public static func affectedAccounts(_ outcomes: [ActivationOutcome]) -> [String] {
        var ids: [String] = []
        for outcome in outcomes {
            guard case .activated = outcome.result, qualifies(outcome.roleKey),
                  !ids.contains(outcome.roleKey.identityId) else { continue }
            ids.append(outcome.roleKey.identityId)
        }
        return ids
    }

    /// Whether a role of this key, once active, is one the cached tokens would not know about.
    public static func qualifies(_ key: RoleKey) -> Bool {
        key.scope.kind == .azureResource || key.scope.kind == .group
    }
}
