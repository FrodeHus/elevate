using System.Text.Json.Serialization;

namespace Elevate.Core.Models;

public enum RoleScopeKind { EntraDirectory, AzureResource, Group }

public enum GroupAccess { Member, Owner }

/// <summary>What a role assignment applies to. Serialised with a "kind" discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntraDirectoryScope), "entraDirectory")]
[JsonDerivedType(typeof(AzureResourceScope), "azureResource")]
[JsonDerivedType(typeof(GroupScope), "group")]
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
