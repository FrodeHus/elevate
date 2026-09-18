using System.Linq;

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
    OrganizationName,
    OrganizationTitleStyle,
    OrganizationSupportUrl,
    OrganizationSupportEmail,
}

/// <summary>
/// How an organization's name is phrased beside Elevate's own. <c>None</c> keeps the support
/// contact and the About section but renders no caption in the panel header. Elevate's own name is
/// never replaced. Port of the Swift <c>OrganizationTitleStyle</c> enum; the wire names are
/// <c>by</c>, <c>managedBy</c> and <c>none</c>.
/// </summary>
public enum OrganizationTitleStyle
{
    By,
    ManagedBy,
    None,
}

/// <summary>Wire names and parsing for <see cref="OrganizationTitleStyle"/>.</summary>
public static class OrganizationTitleStyles
{
    /// <summary>The raw string an administrator pushes.</summary>
    public static string Name(this OrganizationTitleStyle style) => style switch
    {
        OrganizationTitleStyle.By => "by",
        OrganizationTitleStyle.ManagedBy => "managedBy",
        OrganizationTitleStyle.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, message: null),
    };

    /// <summary>Case-insensitive lookup by wire name; null when no style matches.</summary>
    public static OrganizationTitleStyle? Parse(string raw) =>
        Enum.GetValues<OrganizationTitleStyle>()
            .Cast<OrganizationTitleStyle?>()
            .FirstOrDefault(s => string.Equals(s!.Value.Name(), raw, StringComparison.OrdinalIgnoreCase));
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
        ManagedKey.OrganizationName => "OrganizationName",
        ManagedKey.OrganizationTitleStyle => "OrganizationTitleStyle",
        ManagedKey.OrganizationSupportUrl => "OrganizationSupportUrl",
        ManagedKey.OrganizationSupportEmail => "OrganizationSupportEmail",
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
        ManagedKey.OrganizationName,
        ManagedKey.OrganizationTitleStyle,
        ManagedKey.OrganizationSupportUrl,
        ManagedKey.OrganizationSupportEmail,
    ];
}
