namespace Elevate.Core.Models;

/// <summary>
/// The answer to "is this role usable yet?", as distinct from "does PIM call it active?".
/// Port of the Swift <c>EffectiveAccess</c>.
/// </summary>
public enum EffectiveAccessKind
{
    /// <summary>The probe saw the access: a token claim or the resource's own permission check.</summary>
    Confirmed,

    /// <summary>The probe ran and the access is not there yet. Worth asking again.</summary>
    NotYet,

    /// <summary>
    /// The probe could not tell — the access is not observable from the client, or the call that
    /// would have answered failed. Asking again will not help, so the caller stops and says why.
    /// </summary>
    Unknown,
}

/// <summary>
/// One probe's verdict. <see cref="Detail"/> is the reason for <see cref="EffectiveAccessKind.Unknown"/>,
/// written for a user rather than a log. Encoded nowhere: this is live state, never persisted.
/// </summary>
public sealed record EffectiveAccess
{
    private EffectiveAccess(EffectiveAccessKind kind, string? detail)
    {
        Kind = kind;
        Detail = detail;
    }

    public EffectiveAccessKind Kind { get; }

    /// <summary>Why the probe could not tell; null for the other two kinds.</summary>
    public string? Detail { get; }

    public static readonly EffectiveAccess Confirmed = new(EffectiveAccessKind.Confirmed, null);

    public static readonly EffectiveAccess NotYet = new(EffectiveAccessKind.NotYet, null);

    public static EffectiveAccess Unknown(string reason) => new(EffectiveAccessKind.Unknown, reason);
}

/// <summary>
/// How long propagation usually takes, per kind of role, and how long to keep asking before saying
/// so. Microsoft documents the behaviour but not a figure to rely on; these come from the ranges in
/// its own guidance — Entra directory roles a few minutes, Azure resource roles longer, PIM for
/// Groups quick once the membership exists. Port of the Swift <c>PropagationHints</c>.
/// </summary>
public static class PropagationHints
{
    /// <summary>What to tell the user to expect. Shown as "~3 min", so keep these round.</summary>
    public static TimeSpan Typical(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory => TimeSpan.FromMinutes(3),
        RoleScopeKind.AzureResource => TimeSpan.FromMinutes(10),
        RoleScopeKind.Group => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>When to stop probing and name the likely cause instead of waiting silently.</summary>
    public static TimeSpan Deadline(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory => TimeSpan.FromMinutes(6),
        RoleScopeKind.AzureResource => TimeSpan.FromMinutes(20),
        RoleScopeKind.Group => TimeSpan.FromMinutes(6),
        _ => TimeSpan.FromMinutes(10),
    };

    /// <summary>The longest deadline among <paramref name="kinds"/>, for a wait covering several roles.</summary>
    public static TimeSpan Deadline(IEnumerable<RoleScopeKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var longest = TimeSpan.Zero;
        foreach (var kind in kinds)
        {
            var deadline = Deadline(kind);
            if (deadline > longest)
            {
                longest = deadline;
            }
        }

        return longest == TimeSpan.Zero ? Deadline(RoleScopeKind.EntraDirectory) : longest;
    }

    /// <summary>
    /// What most likely explains a role that is active but still not usable, named so the user has
    /// something to do rather than a spinner. Port of the Swift <c>PropagationHints.likelyCause</c>.
    /// </summary>
    public static string LikelyCause(RoleScopeKind kind) => kind switch
    {
        RoleScopeKind.EntraDirectory =>
            "Entra usually publishes a directory role within a few minutes. Anything already signed in — a "
            + "portal tab, az, a PowerShell session — keeps the token it was given before the activation, so "
            + "sign out and back in there.",
        RoleScopeKind.AzureResource =>
            "Azure resource role assignments can take up to 15 minutes to reach every region. Anything already "
            + "signed in keeps its old token; 'az account get-access-token --scope https://management.azure.com/.default' "
            + "after 'az login' picks up the new one.",
        RoleScopeKind.Group =>
            "Group membership reaches a token only when that token is issued. Anything already signed in keeps "
            + "the membership it had at sign-in, so sign out and back in there.",
        _ => "Anything already signed in keeps the token it was given before the activation; sign out and back in there.",
    };
}
