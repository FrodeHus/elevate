namespace Elevate.Core.Models;

/// <summary>
/// A Microsoft admin portal that can be opened straight into one tenant, for the tenant menu's
/// "Open…" submenu. The Ibiza shells (Azure, Entra, Intune) take the tenant id as the first path
/// segment; the Microsoft 365 security shells (Defender, Purview) take it as <c>?tid=</c>. The
/// Microsoft 365 admin center has no tenant-in-URL form and is deliberately absent. None of these
/// carry the signed-in account: the browser session decides, and Microsoft's sign-in page prompts
/// with the tenant already fixed when it has none.
/// </summary>
public sealed record AdminPortal(string Title, string Host, bool TenantInQuery)
{
    public static readonly AdminPortal Azure = new("Azure Portal", "portal.azure.com", TenantInQuery: false);
    public static readonly AdminPortal Entra = new("Entra admin center", "entra.microsoft.com", TenantInQuery: false);
    public static readonly AdminPortal Intune = new("Intune admin center", "intune.microsoft.com", TenantInQuery: false);
    public static readonly AdminPortal Defender = new("Defender Portal", "security.microsoft.com", TenantInQuery: true);
    public static readonly AdminPortal Purview = new("Purview Portal", "purview.microsoft.com", TenantInQuery: true);

    /// <summary>Menu order.</summary>
    public static IReadOnlyList<AdminPortal> All { get; } = [Azure, Entra, Intune, Defender, Purview];

    /// <summary>The portal opened in <paramref name="tenantId"/>.</summary>
    public Uri Uri(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var escaped = System.Uri.EscapeDataString(tenantId);
        return new Uri(TenantInQuery ? $"https://{Host}/?tid={escaped}" : $"https://{Host}/{escaped}");
    }
}
