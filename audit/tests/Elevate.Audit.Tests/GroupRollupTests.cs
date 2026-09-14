using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class GroupRollupTests
{
    private static readonly FindingRole Ga = new("r-ga", "Global Administrator", null, true, RoleSystem.Entra);
    private static readonly FindingScope Dir = new("/", "Directory", ScopeKind.Directory);
    private static readonly GroupRef Top = new("g-top", "Tier 0 Admins");
    private static readonly GroupRef Plat = new("g-plat", "Platform Team");
    private static readonly GroupRef Ops = new("g-ops", "Ops Leads");
    private static readonly GroupRef Shared = new("g-shared", "Shared Pool");

    private static Finding Person(string id, IReadOnlyList<GroupRef> via, string code = "ENTRA-GROUP-PERMANENT", FindingRole? role = null, FindingScope? scope = null, bool guest = false) =>
        new(code, Severity.High, new FindingPrincipal(id, "Person " + id, id + "@contoso.com", PrincipalType.User, guest, true), role ?? Ga, scope ?? Dir, via,
            "remedy " + id, "https://portal.azure.com/#" + id, new FindingEvidence("a1", null, null, AssignmentType.Assigned, null));

    private static Finding Group(GroupRef g, string code = "ENTRA-GROUP-PERMANENT") =>
        new(code, Severity.High, new FindingPrincipal(g.Id, g.DisplayName, null, PrincipalType.Group, false, null), Ga, Dir, [],
            "group remedy", "https://portal.azure.com/#" + g.Id, new FindingEvidence("a1", null, null, AssignmentType.Assigned, null));

    /// <summary>One assigned group, three nested groups (Shared reached under both Platform and Ops), 120 people.</summary>
    private static List<Finding> Fixture()
    {
        var list = new List<Finding> { Group(Top) };
        for (var i = 0; i < 5; i++) list.Add(Person($"top{i}", [Top]));
        for (var i = 0; i < 40; i++) list.Add(Person($"plat{i}", [Top, Plat]));
        for (var i = 0; i < 30; i++) list.Add(Person($"ops{i}", [Top, Ops]));
        for (var i = 0; i < 25; i++) list.Add(Person($"sp{i}", [Top, Plat, Shared]));
        for (var i = 0; i < 20; i++) list.Add(Person($"so{i}", [Top, Ops, Shared]));
        return list;
    }

    [Theory]
    [InlineData("ENTRA-GROUP-PERMANENT", true)]
    [InlineData("AZURE-PERMANENT", true)]
    [InlineData("GROUP-MEMBER-PERMANENT", true)]
    [InlineData("ENTRA-USER-PERMANENT", false)]
    [InlineData("GUEST-PERMANENT", false)]
    public void RollsUp_OnlyTheGroupRules(string code, bool expected)
    {
        GroupRollup.RollsUp(code).Should().Be(expected);
    }

    [Fact]
    public void Build_OneCardPerAssignedGroup_WithEveryMember()
    {
        var (cards, rows) = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture());

        rows.Should().BeEmpty();
        var card = cards.Should().ContainSingle().Subject;
        card.Group.Id.Should().Be("g-top");
        card.GroupFinding.Should().NotBeNull();
        card.Remedy.Should().Be("group remedy");
        card.Members.Should().HaveCount(120);
        card.People.Should().Be(120);
        card.NestedGroups.Should().Be(4, "Platform, Ops, and Shared under each of them");
    }

    [Fact]
    public void Tree_IsATrieOfViaPaths_WithPeopleAtTheirLastGroup()
    {
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture()).Cards[0];
        var tree = card.Tree;

        tree.Id.Should().Be("g-top");
        tree.People.Should().HaveCount(5);
        tree.Children.Select(c => c.Name).Should().Equal("Platform Team", "Ops Leads");
        tree.Children[0].People.Should().HaveCount(40);
        tree.Children[0].Children.Should().ContainSingle().Which.Name.Should().Be("Shared Pool");
        tree.Children[0].Children[0].People.Should().HaveCount(25);
        tree.Children[1].Children[0].People.Should().HaveCount(20);
        tree.TotalPeople.Should().Be(120);
        tree.GroupCount.Should().Be(5);
    }

    [Fact]
    public void Build_SeparatesCardsByRoleAndScope_AndKeepsDirectFindingsAsRows()
    {
        var reader = new FindingRole("r-reader", "Reader", null, false, RoleSystem.Azure);
        var sub = new FindingScope("/subscriptions/a", "Prod", ScopeKind.Subscription);
        var f = new List<Finding>
        {
            Group(Top, "AZURE-PERMANENT") with { Role = reader, Scope = sub },
            Person("u1", [Top], "AZURE-PERMANENT", reader, sub),
            Person("u2", [], "AZURE-PERMANENT", reader, sub),
            Group(Top, "AZURE-PERMANENT") with { Scope = new FindingScope("/subscriptions/b", "Dev", ScopeKind.Subscription), Role = reader },
        };

        var (cards, rows) = GroupRollup.Build("AZURE-PERMANENT", f);

        cards.Should().HaveCount(2);
        cards[0].Members.Should().ContainSingle().Which.Principal.Id.Should().Be("u1");
        cards[1].Members.Should().BeEmpty();
        rows.Should().ContainSingle().Which.Principal.Id.Should().Be("u2");
    }

    [Fact]
    public void Build_SynthesisesACard_WhenTheGroupFindingIsMissing()
    {
        var (cards, _) = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Person("u1", [Top, Plat]), Person("u2", [Top])]);

        var card = cards.Should().ContainSingle().Subject;
        card.GroupFinding.Should().BeNull();
        card.Group.Id.Should().Be("g-top");
        card.Group.DisplayName.Should().Be("Tier 0 Admins");
        card.Group.Type.Should().Be(PrincipalType.Group);
        card.Remedy.Should().Be("remedy u1");
        card.Members.Should().HaveCount(2);
    }

    [Fact]
    public void Build_PimGroupMembers_RollUpByTheScopeGroup()
    {
        var g1 = new FindingScope("g-pim1", "Tier 0 Admins", ScopeKind.Group);
        var g2 = new FindingScope("g-pim2", "Helpdesk", ScopeKind.Group);
        var member1 = new FindingRole("g-pim1", "Tier 0 Admins (member)", null, true, RoleSystem.Group);
        var owner1 = new FindingRole("g-pim1", "Tier 0 Admins (owner)", null, true, RoleSystem.Group);
        var member2 = new FindingRole("g-pim2", "Helpdesk (member)", null, true, RoleSystem.Group);
        var f = new List<Finding>
        {
            Person("u1", [], "GROUP-MEMBER-PERMANENT", member1, g1),
            Person("u2", [], "GROUP-MEMBER-PERMANENT", owner1, g1),
            Person("u3", [], "GROUP-MEMBER-PERMANENT", member2, g2),
        };

        var (cards, rows) = GroupRollup.Build("GROUP-MEMBER-PERMANENT", f);

        rows.Should().BeEmpty();
        cards.Select(c => c.Group.Id).Should().Equal("g-pim1", "g-pim1", "g-pim2");
        cards[0].Role.DisplayName.Should().Be("Tier 0 Admins (member)");
        cards[0].NestedGroups.Should().Be(0);
        cards[0].Tree.People.Should().ContainSingle();
    }

    [Fact]
    public void Outline_ListsGroupsWithCounts_InlinesSmallGroups_AndSummarisesLargeOnes()
    {
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture()).Cards[0];

        var html = GroupRollup.Outline(card);

        html.Should().StartWith("<ul class=\"tree\">").And.EndWith("</ul>");
        html.Should().Contain("Global Administrator");
        html.Should().Contain("Tier 0 Admins").And.Contain("5 people direct · 2 nested groups");
        html.Should().Contain("Person top0"); // 5 direct people are inlined
        html.Should().NotContain("Person plat0"); // 40 are not
        html.Should().Contain("40 people, listed below");
        html.Should().Contain("Shared Pool");
        System.Text.RegularExpressions.Regex.Matches(html, "<ul").Count.Should().Be(System.Text.RegularExpressions.Regex.Matches(html, "</ul>").Count);
        System.Text.RegularExpressions.Regex.IsMatch(html, "<ul[^>]*>\\s*<ul").Should().BeFalse("nested lists live inside an <li>");
    }

    [Fact]
    public void Outline_MarksGuests_AndEscapes()
    {
        var evil = new GroupRef("g-x", "<Evil>");
        var (cards, _) = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Group(evil), Person("u1", [evil], guest: true)]);

        var html = GroupRollup.Outline(cards[0]);

        html.Should().Contain("&lt;Evil&gt;").And.NotContain("<Evil>");
        html.Should().Contain("<span class=\"pill\">guest</span>");
    }

    [Fact]
    public void Diagram_DrawsRoleAndEveryGroup_WithPeopleCounts()
    {
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture()).Cards[0];

        var svg = GroupRollup.Diagram(card);

        svg.Should().NotBeNull();
        svg.Should().StartWith("<svg").And.EndWith("</svg>");
        svg.Should().Contain("Global Administrator").And.Contain("Tier 0 Admins").And.Contain("Platform Team").And.Contain("Ops Leads");
        System.Text.RegularExpressions.Regex.Matches(svg!, "<rect").Count.Should().Be(6, "role + 5 group nodes");
        System.Text.RegularExpressions.Regex.Matches(svg!, "<path").Count.Should().Be(5, "one edge per non-root node plus role→group");
        svg.Should().Contain("5 people").And.Contain("40 people").And.Contain("25 people");
        svg.Should().NotContain("Person ");
        svg.Should().Contain("viewBox=\"0 0 ");
    }

    [Fact]
    public void Diagram_IsOmitted_WithoutNesting_OrAboveTheGroupCap()
    {
        var flat = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Group(Top), Person("u1", [Top])]).Cards[0];
        GroupRollup.Diagram(flat).Should().BeNull();

        var big = new List<Finding> { Group(Top) };
        for (var i = 0; i < GroupRollup.MaxDiagramGroups; i++)
        {
            big.Add(Person($"u{i}", [Top, new GroupRef($"g{i}", $"Group {i}")]));
        }

        GroupRollup.Diagram(GroupRollup.Build("ENTRA-GROUP-PERMANENT", big).Cards[0]).Should().BeNull("13 groups exceed the cap of 12");
        big.RemoveAt(big.Count - 1);
        GroupRollup.Diagram(GroupRollup.Build("ENTRA-GROUP-PERMANENT", big).Cards[0]).Should().NotBeNull("12 groups are within the cap");
    }

    [Fact]
    public void Diagram_EllipsisesLongNames_AndKeepsTheFullNameInATitle()
    {
        var longName = new GroupRef("g-long", "A Very Long Group Name Indeed");
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Group(Top), Person("u1", [Top, longName])]).Cards[0];

        var svg = GroupRollup.Diagram(card)!;

        svg.Should().Contain("A Very Long Group…").And.Contain("<title>A Very Long Group Name Indeed</title>");
    }
}
