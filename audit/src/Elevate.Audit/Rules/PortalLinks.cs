namespace Elevate.Audit.Rules;

/// <summary>Deep links into the Entra admin center and the Azure portal, for remedies.</summary>
public static class PortalLinks
{
    public const string EntraRoles = "https://entra.microsoft.com/#view/Microsoft_Azure_PIMCommon/ResourceMenuBlade/~/roleassignments/resourceId//resourceType/tenant/provider/aadroles";

    public static string Group(string groupId) => $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupDetailsMenuBlade/~/Overview/groupId/{Uri.EscapeDataString(groupId)}";

    public static string GroupPim(string groupId) => $"https://entra.microsoft.com/#view/Microsoft_Azure_PIMCommon/ResourceMenuBlade/~/members/resourceId/{Uri.EscapeDataString(groupId)}/resourceType/Security/provider/aadgroup";

    public static string User(string userId) => $"https://entra.microsoft.com/#view/Microsoft_AAD_UsersAndTenants/UserProfileMenuBlade/~/overview/userId/{Uri.EscapeDataString(userId)}";

    public static string AzureScope(string tenantId, string scope) => $"https://portal.azure.com/#@{tenantId}/resource{scope}/users";
}
