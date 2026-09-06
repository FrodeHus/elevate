using System.Text.Json.Serialization;

namespace Elevate.Core.Models;

/// <summary>A signed-in Entra user. <see cref="Id"/> is MSAL's home account identifier.</summary>
public sealed record Identity(
    string Id,
    string Upn,
    string DisplayName,
    string HomeTenantId,
    SignInMethod SignInMethod = default);

/// <summary>Identifies one identity acting in one tenant.</summary>
public readonly record struct TenantKey(string IdentityId, string TenantId);

public enum TenantSource { Home, Discovered, Manual }

public enum DiscoveryMode { Automatic, ManualRoles }

/// <summary>
/// Whether the signed-in account may activate Entra directory roles in a tenant, as proven by the
/// scopes Microsoft actually put in its Graph token (or by a refused activation).
/// <see cref="Reason"/> is null when activation is supported.
/// </summary>
public sealed record EntraActivationSupport(string? Reason)
{
    public static readonly EntraActivationSupport Supported = new((string?)null);

    public static EntraActivationSupport Unsupported(string reason) => new(reason);

    [JsonIgnore]
    public bool IsSupported => Reason is null;
}

/// <summary>One tenant an identity can act in. The same identity may have many.</summary>
public sealed record TenantContext(
    string IdentityId,
    string TenantId,
    string DisplayName,
    TenantSource Source,
    DiscoveryMode DiscoveryMode = DiscoveryMode.Automatic,
    // Object id of the identity inside this tenant (guests differ per tenant).
    string? PrincipalObjectId = null,
    string? LastDiscoveryError = null,
    // Set when Azure resource reads are pointless in this tenant; the provider is skipped while set.
    string? AzureUnavailableReason = null,
    // What the Graph token in this tenant allows for Entra roles; null until a refresh has looked at one.
    EntraActivationSupport? EntraActivation = null,
    // Set when group PIM reads are not permitted in this tenant (missing admin consent).
    string? GroupsUnavailableReason = null)
{
    [JsonIgnore]
    public TenantKey Key => new(IdentityId, TenantId);
}
