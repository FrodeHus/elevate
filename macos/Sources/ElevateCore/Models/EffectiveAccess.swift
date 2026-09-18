import Foundation

/// The answer to "is this role usable yet?", as distinct from "does PIM call it active?".
///
/// PIM reports an assignment active as soon as it writes it; the access behind it arrives minutes
/// later, and that gap is the most-repeated complaint about PIM.
public enum EffectiveAccess: Hashable, Sendable {
    /// The probe saw the access: a token claim or the resource's own permission check.
    case confirmed
    /// The probe ran and the access is not there yet. Worth asking again.
    case notYet
    /// The probe could not tell — the access is not observable from the client, or the call that
    /// would have answered failed. Asking again will not help, so the caller stops and says why.
    /// The string is the reason, written for a user rather than a log.
    case unknown(String)
}

/// How long propagation usually takes, per kind of role, and how long to keep asking before saying
/// so. Microsoft documents the behaviour but not a figure to rely on; these come from the ranges in
/// its own guidance — Entra directory roles a few minutes, Azure resource roles longer, PIM for
/// Groups quick once the membership exists.
public enum PropagationHints {
    /// What to tell the user to expect. Shown as "~3 min", so keep these round.
    public static func typical(_ kind: RoleScopeKind) -> TimeInterval {
        switch kind {
        case .entraDirectory: 3 * 60
        case .azureResource: 10 * 60
        case .group: 2 * 60
        }
    }

    /// When to stop probing and name the likely cause instead of waiting silently.
    public static func deadline(_ kind: RoleScopeKind) -> TimeInterval {
        switch kind {
        case .entraDirectory: 6 * 60
        case .azureResource: 20 * 60
        case .group: 6 * 60
        }
    }

    /// The longest deadline among `kinds`, for a wait covering several roles.
    public static func deadline(_ kinds: some Sequence<RoleScopeKind>) -> TimeInterval {
        kinds.map { deadline($0) }.max() ?? deadline(.entraDirectory)
    }

    /// What most likely explains a role that is active but still not usable, named so the user has
    /// something to do rather than a spinner.
    public static func likelyCause(_ kind: RoleScopeKind) -> String {
        switch kind {
        case .entraDirectory:
            "Entra usually publishes a directory role within a few minutes. Anything already signed in — a "
                + "portal tab, az, a PowerShell session — keeps the token it was given before the activation, so "
                + "sign out and back in there."
        case .azureResource:
            "Azure resource role assignments can take up to 15 minutes to reach every region. Anything already "
                + "signed in keeps its old token; 'az account get-access-token --scope https://management.azure.com/.default' "
                + "after 'az login' picks up the new one."
        case .group:
            "Group membership reaches a token only when that token is issued. Anything already signed in keeps "
                + "the membership it had at sign-in, so sign out and back in there."
        }
    }
}
