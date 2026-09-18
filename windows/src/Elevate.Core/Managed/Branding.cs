namespace Elevate.Core.Managed;

/// <summary>
/// The organization's co-branding, resolved from <see cref="ManagedConfiguration"/> into the exact
/// strings each surface renders. Resolving to null is the unbranded case: every caller renders
/// exactly what it rendered before this type existed, so an unbranded install is unaffected.
///
/// Elevate's own name is never replaced — the organization's name is added beside it. Port of the
/// Swift <c>Branding</c> struct; see
/// <c>docs/superpowers/specs/2026-09-18-enterprise-cobranding-design.md</c>.
/// </summary>
public sealed record Branding(
    string OrganizationName,
    OrganizationTitleStyle TitleStyle,
    Uri? SupportUrl,
    string? SupportEmail)
{
    /// <summary>The panel header's second line, under "Elevate". Null for the None style.</summary>
    public string? HeaderCaption => TitleStyle switch
    {
        OrganizationTitleStyle.By => $"by {OrganizationName}",
        OrganizationTitleStyle.ManagedBy => $"Managed by {OrganizationName}",
        _ => null,
    };

    public string FirstRunLine => $"Provided by {OrganizationName}.";

    public string SupportLabel => $"{OrganizationName} IT";

    public bool HasSupport => SupportUrl is not null || SupportEmail is not null;

    /// <summary>
    /// The one-line contact appended to CLI failures. Prefers the URL: a help desk portal lists the
    /// address, but an address does not list the portal.
    /// </summary>
    public string? SupportLine =>
        (SupportUrl?.ToString() ?? SupportEmail) is { } target
            ? $"Need help? {SupportLabel} — {target}"
            : null;

    public static Branding? Resolve(ManagedConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.OrganizationName is not { } name
            ? null
            : new Branding(
                name,
                config.OrganizationTitleStyle ?? OrganizationTitleStyle.By,
                config.OrganizationSupportUrl,
                config.OrganizationSupportEmail);
    }
}
