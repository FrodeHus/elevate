using Elevate.Core.Models;

namespace Elevate.Core.Auth;

/// <summary>Delegated scope sets, with the same URL strings as the Swift <c>GraphScopes</c>/<c>ArmScopes</c>/<c>GroupScopes</c>.</summary>
public static class Scopes
{
    public const string GraphUserRead = "https://graph.microsoft.com/User.Read";

    public static IReadOnlyList<string> GraphAll { get; } =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/RoleEligibilitySchedule.Read.Directory",
        "https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory",
        "https://graph.microsoft.com/RoleManagementPolicy.Read.Directory",
    ];

    public static IReadOnlyList<string> ArmAll { get; } = ["https://management.azure.com/user_impersonation"];

    /// <summary>Delegated Graph permissions for PIM for Groups. Admin-consent only, like the Entra PIM scopes.</summary>
    public static IReadOnlyList<string> GroupAll { get; } =
    [
        "https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup",
        "https://graph.microsoft.com/PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup",
        "https://graph.microsoft.com/RoleManagementPolicy.Read.AzureADGroup",
    ];
}

/// <summary>Port of the Swift <c>TokenProviding</c> protocol.</summary>
public interface ITokenProvider
{
    /// <summary>Interactive sign-in against the <c>organizations</c> authority. Returns the new identity.</summary>
    Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct = default);

    Task SignOutAsync(Identity identity, CancellationToken ct = default);

    Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Silent acquisition for <paramref name="tenantId"/>. Throws
    /// <see cref="PimException"/> with <see cref="PimErrorKind.InteractionRequired"/> when a prompt is needed.
    /// </summary>
    Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default);

    /// <summary>
    /// Interactive acquisition, optionally carrying a claims challenge. Throws
    /// <see cref="PimErrorKind.ConsentRequired"/> on AADSTS65001.
    /// </summary>
    Task<string> AcquireInteractivelyAsync(
        Identity identity,
        string tenantId,
        IReadOnlyList<string> scopes,
        string? claims,
        CancellationToken ct = default);
}

/// <summary>Port of the Swift <c>InteractionRetry</c>: one interactive attempt, one retry.</summary>
public static class InteractionRetry
{
    /// <summary>
    /// Runs <paramref name="operation"/>; on <see cref="PimErrorKind.InteractionRequired"/> or
    /// <see cref="PimErrorKind.ClaimsChallenge"/> acquires a token interactively once and retries once.
    /// <paramref name="fallbackClaims"/> is sent when the service demanded interaction without saying
    /// which claims it wants (a PIM MFA rule), so the browser actually re-verifies instead of
    /// silently reusing the session. Any other failure — including a second failure of the operation
    /// and a failed interactive acquisition — propagates unchanged.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        ITokenProvider tokens,
        Identity identity,
        string tenantId,
        IReadOnlyList<string> scopes,
        Func<Task<T>> operation,
        string? fallbackClaims = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(operation);

        string? claims;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.InteractionRequired)
        {
            claims = fallbackClaims;
        }
        catch (PimException e) when (e.Kind is PimErrorKind.ClaimsChallenge)
        {
            claims = e.Detail;
        }

        await tokens.AcquireInteractivelyAsync(identity, tenantId, scopes, claims, ct).ConfigureAwait(false);

        return await operation().ConfigureAwait(false);
    }
}
