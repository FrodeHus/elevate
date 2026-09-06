using System.Text.Json.Serialization;
using Elevate.Core;

namespace Elevate.Core.Models;

public enum RoleScopeKind { EntraDirectory, AzureResource, Group }

public enum GroupAccess { Member, Owner }

/// <summary>
/// What a role assignment applies to. Serialised exactly like Swift's synthesised enum coding:
/// a single-key object keyed by the case name, e.g.
/// <c>{"entraDirectory":{"roleDefinitionId":"…","directoryScopeId":"…"}}</c>.
/// </summary>
[JsonConverter(typeof(RoleScopeJsonConverter))]
public abstract record RoleScope
{
    [JsonIgnore]
    public abstract RoleScopeKind Kind { get; }
}

public sealed record EntraDirectoryScope(string RoleDefinitionId, string DirectoryScopeId) : RoleScope
{
    [JsonIgnore]
    public override RoleScopeKind Kind => RoleScopeKind.EntraDirectory;
}

public sealed record AzureResourceScope(string Scope, string RoleDefinitionId) : RoleScope
{
    [JsonIgnore]
    public override RoleScopeKind Kind => RoleScopeKind.AzureResource;
}

public sealed record GroupScope(string GroupId, GroupAccess AccessId) : RoleScope
{
    [JsonIgnore]
    public override RoleScopeKind Kind => RoleScopeKind.Group;
}

/// <summary>Identity + tenant + scope: the stable identity of one role a user can hold.</summary>
public sealed record RoleKey(string IdentityId, string TenantId, RoleScope Scope)
{
    [JsonIgnore]
    public TenantKey TenantKey => new(IdentityId, TenantId);
}
