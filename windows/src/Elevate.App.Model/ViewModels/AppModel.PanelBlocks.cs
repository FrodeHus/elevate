using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

public enum PanelBlockKind { Approvals, ActiveNow, EmptyCard, Eligible, BrowseAll, SelectRoles, SearchAllTenants }

/// <summary>
/// One block of the main view's list, top to bottom. <paramref name="Count"/> is the block's
/// headline number, <paramref name="Other"/> a second one (tenants for Browse all roles),
/// <paramref name="Hero"/> the Active now row drawn as a card, <paramref name="NoMatches"/> an
/// Eligible block whose filter matched nothing.
/// </summary>
public sealed record PanelBlock(PanelBlockKind Kind, int Count = 0, int Other = 0, RoleKey? Hero = null, bool NoMatches = false);

/// <summary>What the main view's list holds for the current content. Port of the macOS Home and role list views.</summary>
public sealed partial class AppModel
{
    public IReadOnlyList<PanelBlock> PanelBlocks(DateTimeOffset now)
    {
        var blocks = new List<PanelBlock>();
        switch (PanelContent)
        {
            case PanelContent.Home:
                AddPending(blocks, null, HeroAssignment(now)?.RoleKey);
                blocks.Add(new PanelBlock(PanelBlockKind.BrowseAll, Roles.Values.Sum(r => r.Count), State.Tenants.Count));
                break;
            case PanelContent.RoleList:
            {
                var idle = HomeIsEmpty && !IsFiltering;
                if (idle)
                {
                    blocks.Add(new PanelBlock(PanelBlockKind.EmptyCard));
                }

                var count = VisibleIdentities.Sum(i => VisibleTenants(i.Id).Sum(t => RolesFor(t.Key, PanelTab).Count));
                blocks.Add(new PanelBlock(PanelBlockKind.Eligible, count, NoMatches: IsFiltering && count == 0));
                if (idle && Profiles.Count == 0 && !SelectMode)
                {
                    blocks.Add(new PanelBlock(PanelBlockKind.SelectRoles));
                }

                break;
            }

            case PanelContent.Tenant t:
            {
                AddPending(blocks, t.Key, null);
                var count = RolesFor(t.Key, PanelTab).Count;
                blocks.Add(new PanelBlock(PanelBlockKind.Eligible, count, NoMatches: IsFiltering && count == 0));
                if (OtherTenantMatchCount is > 0 and var others)
                {
                    blocks.Add(new PanelBlock(PanelBlockKind.SearchAllTenants, others));
                }

                break;
            }

            default:
                break;
        }

        return blocks;
    }

    private void AddPending(List<PanelBlock> blocks, TenantKey? tenant, RoleKey? hero)
    {
        if (ApprovalsIn(tenant).Count is > 0 and var approvals)
        {
            blocks.Add(new PanelBlock(PanelBlockKind.Approvals, approvals));
        }

        if (ActiveAssignments(tenant).Count is > 0 and var active)
        {
            blocks.Add(new PanelBlock(PanelBlockKind.ActiveNow, active, Hero: hero));
        }
    }
}
