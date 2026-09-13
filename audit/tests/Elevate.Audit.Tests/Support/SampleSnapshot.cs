using Elevate.Audit.Model;

namespace Elevate.Audit.Tests.Support;

public static class SampleSnapshot
{
    public const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    public const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    public const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    public static Snapshot Build() => SnapshotBuilder.Contoso()
        .Tenant("72f988bf-0000-4000-8000-2d7cd011db47", "Contoso")
        .EntraRole("rd-sec", "194ae4cb-b126-40b2-bd5b-6091b380977d", "Security Administrator", privileged: true)
        .User("u-alex", "Alex Rivera", "alex.rivera@contoso.com")
        .User("u-sam", "Sam Chen", "sam.chen@contoso.com")
        .User("u-priya", "Priya Natarajan", "priya.natarajan_fabrikam.com#EXT#@contoso.com", guest: true)
        .User("u-jordan", "Jordan Lee", "jordan.lee@contoso.com")
        .User("u-casey", "Casey Wong", "casey.wong@contoso.com")
        .User("u-riley", "Riley Park", "riley.park@contoso.com")
        .User("u-morgan", "Morgan Diaz", "morgan.diaz@contoso.com")
        .ServicePrincipal("sp-deploy", "Deploy Bot")
        .Group("g-tier0", "Tier 0 Admins", pim: PimStatus.Onboarded, members: [("u-sam", PrincipalType.User), ("g-platform", PrincipalType.Group)])
        .Group("g-platform", "Platform Team", assignable: false, members: [("u-casey", PrincipalType.User), ("g-tier0", PrincipalType.Group)])
        .Group("g-legacy", "Legacy Ops", assignable: false, pim: PimStatus.NotOnboarded, members: [("u-riley", PrincipalType.User)])
        .Group("g-helpdesk", "Helpdesk Leads", pim: PimStatus.NotOnboarded, members: [("u-morgan", PrincipalType.User)])
        .Group("g-cloudops", "Cloud Ops", assignable: false, members: [("u-jordan", PrincipalType.User)])
        .Assigned("a-alex-ga", "u-alex", "rd-ga")
        .Assigned("a-tier0-ga", "g-tier0", "rd-ga")
        .Assigned("a-tier0-ga-sam", "u-sam", "rd-ga", memberType: "Group")
        .Assigned("a-legacy-sec", "g-legacy", "rd-sec")
        .Assigned("a-helpdesk-pra", "g-helpdesk", "rd-pra")
        .Assigned("a-priya-sec", "u-priya", "rd-sec")
        .Assigned("a-deploy-ga", "sp-deploy", "rd-ga")
        .Assigned("a-jordan-ga-active", "u-jordan", "rd-ga", type: AssignmentType.Activated, end: new DateTimeOffset(2026, 9, 13, 20, 0, 0, TimeSpan.Zero))
        .Assigned("a-alex-reader", "u-alex", "rd-reader")
        .Eligible("e-jordan-ga", "u-jordan", "rd-ga")
        .Eligible("e-casey-ga", "u-casey", "rd-ga", end: new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero))
        .Eligible("e-riley-ga", "u-riley", "rd-ga")
        .Eligible("e-morgan-ga", "u-morgan", "rd-ga")
        .GroupPim("g-tier0", "gp-sam-owner", "u-sam", "owner")
        .GroupPim("g-tier0", "gp-alex-member", "u-alex", "member", eligible: true)
        .AzureScope("/subscriptions/sub1/resourceGroups/rg-payments", AzureScopeKind.ResourceGroup, "rg-payments")
        .AzureAssigned("ra-alex-owner", "/subscriptions/sub1", Owner, "u-alex", "User")
        .AzureAssigned("ra-cloudops-contrib", "/subscriptions/sub1/resourceGroups/rg-payments", Contributor, "g-cloudops", "Group")
        .AzureAssigned("ra-deploy-owner", "/subscriptions/sub1", Owner, "sp-deploy", "ServicePrincipal")
        .AzureAssigned("ra-sam-owner-classic", "/subscriptions/sub1", Owner, "u-sam", "User")
        .AzureAssigned("si-sam-owner-active", "/subscriptions/sub1", Owner, "u-sam", "User", type: AssignmentType.Activated, end: new DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.Zero), fromSchedule: true)
        .AzureAssigned("ra-jordan-reader", "/subscriptions/sub1", Reader, "u-jordan", "User")
        .AzureEligible("ae-jordan-owner", "/subscriptions/sub1", Owner, "u-jordan", "User")
        .Skipped("azure-management-groups", "Azure management groups are not readable by this account; scanned subscriptions only.")
        .Build();
}
