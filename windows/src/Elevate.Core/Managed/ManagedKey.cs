namespace Elevate.Core.Managed;

/// <summary>
/// The keys an MDM/GPO administrator can push through managed configuration (registry policy on
/// Windows, <c>managed.json</c> as a fallback, or a plain dictionary in tests). Port of the Swift
/// <c>ManagedKey</c> enum.
/// </summary>
public enum ManagedKey
{
    ClientId,
    DisableUpdateCheck,
    AllowedSignInMethods,
    AllowedTenants,
    PinnedTenants,
    ManagedProfiles,
    ManagedProfilesUrl,
}

/// <summary>Raw string names and enumeration helpers for <see cref="ManagedKey"/>.</summary>
public static class ManagedKeys
{
    /// <summary>The raw string name used as a registry value name or JSON property name.</summary>
    public static string Name(this ManagedKey key) => key switch
    {
        ManagedKey.ClientId => "ClientId",
        ManagedKey.DisableUpdateCheck => "DisableUpdateCheck",
        ManagedKey.AllowedSignInMethods => "AllowedSignInMethods",
        ManagedKey.AllowedTenants => "AllowedTenants",
        ManagedKey.PinnedTenants => "PinnedTenants",
        ManagedKey.ManagedProfiles => "ManagedProfiles",
        ManagedKey.ManagedProfilesUrl => "ManagedProfilesUrl",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, message: null),
    };

    /// <summary>Every key, in declaration order.</summary>
    public static IReadOnlyList<ManagedKey> All { get; } =
    [
        ManagedKey.ClientId,
        ManagedKey.DisableUpdateCheck,
        ManagedKey.AllowedSignInMethods,
        ManagedKey.AllowedTenants,
        ManagedKey.PinnedTenants,
        ManagedKey.ManagedProfiles,
        ManagedKey.ManagedProfilesUrl,
    ];
}
