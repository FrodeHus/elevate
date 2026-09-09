using Elevate.Core.Models;

namespace Elevate.Core.Managed;

/// <summary>
/// Pure decisions about what a managed configuration permits. No I/O, no state: the callers
/// (sign-in flows, account lists, diagnostics) ask these before offering or keeping an account.
/// Port of the Swift <c>ManagedPolicy</c> enum.
/// </summary>
public static class ManagedPolicy
{
    /// <summary>
    /// Whether <paramref name="method"/> may be used under <paramref name="config"/>. With no
    /// <see cref="ManagedConfiguration.AllowedSignInMethods"/> key in effect every method is
    /// allowed; otherwise the method's <see cref="SignInMethod.Kind"/> must be in the list, so
    /// <see cref="SignInMethodKind.Custom"/> in the list permits any custom client id.
    /// </summary>
    public static bool IsAllowed(SignInMethod method, ManagedConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.AllowedSignInMethods is not { } allowed || allowed.Contains(method.Kind);
    }

    /// <summary>
    /// Whether <paramref name="tenantId"/> is in <paramref name="allowedIds"/>, comparing
    /// case-insensitively. A null list — the <c>AllowedTenants</c> key not in effect — allows
    /// every tenant.
    /// </summary>
    public static bool IsTenantAllowed(string tenantId, IReadOnlySet<string>? allowedIds)
        => allowedIds is null
            || allowedIds.Any(id => string.Equals(id, tenantId, StringComparison.OrdinalIgnoreCase));
}
