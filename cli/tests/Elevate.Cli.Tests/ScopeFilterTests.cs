using Elevate.Cli.Infrastructure;
using Elevate.Cli.Selection;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// The scripted half of the Azure scope hierarchy: <c>--under</c> reaches a subtree, a glob
/// <c>--scope</c> reaches a pattern, and <c>--all</c> lets one term take everything it matched.
/// </summary>
public class ScopeFilterTests
{
    private const string SubA = "/subscriptions/aaaaaaaa-0000-0000-0000-000000000000";
    private const string SubB = "/subscriptions/bbbbbbbb-0000-0000-0000-000000000000";
    private const string Mg = "/providers/Microsoft.Management/managementGroups/platform";

    private static RoleKey AzureKey(string scope, string roleDefinitionId) =>
        new("id1", "t1", new AzureResourceScope(scope, roleDefinitionId));

    private static EligibleRole Azure(string scope, string name, string? detail = null) =>
        new(AzureKey(scope, "rd-" + name + "-" + scope), name, RoleSource.Discovered, RolePolicy.ManualDefault, Detail: detail);

    /// <summary>A tenant eligible for Contributor across two subscriptions, plus a few neighbours.</summary>
    private static async Task<TestSession> SeedAsync()
    {
        var t = new TestSession();
        t.Azure.Eligible.Add(Azure(SubA, "Contributor", "Alpha · subscription"));
        t.Azure.Eligible.Add(Azure(SubB, "Contributor", "Bravo · subscription"));
        t.Azure.Eligible.Add(Azure(SubA + "/resourceGroups/prod-rg", "Owner", "prod-rg · resource group"));
        t.Azure.Eligible.Add(Azure(SubA + "/resourceGroups/test-rg", "Reader", "test-rg · resource group"));
        t.Azure.Eligible.Add(Azure(Mg, "Reader", "Platform · management group"));
        t.Entra.Eligible.Add(TestSession.Role("r1", "Global Reader"));
        await t.Session.RefreshAllAsync();
        return t;
    }

    [Fact]
    public async Task UnderTakesEverythingAtOrBelowASubscription()
    {
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Under: SubA))
            .Select(r => r.DisplayName).Should().BeEquivalentTo("Contributor", "Owner", "Reader");
    }

    [Fact]
    public async Task UnderAcceptsABareResourceGroupName()
    {
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Under: "prod-rg"))
            .Should().ContainSingle().Which.DisplayName.Should().Be("Owner");
    }

    [Fact]
    public async Task UnderAcceptsASubscriptionIdOnItsOwn()
    {
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Under: "aaaaaaaa-0000-0000-0000-000000000000")).Should().HaveCount(3);
    }

    [Fact]
    public async Task UnderNeverMatchesANonAzureRole()
    {
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Under: "/"))
            .Should().OnlyContain(r => r.Key.Scope.Kind == RoleScopeKind.AzureResource);
    }

    [Fact]
    public async Task UnderAManagementGroupCoversOnlyItsOwnEligibilities()
    {
        // ARM does not record which subscriptions sit under a management group, so the scope
        // string cannot reach them. The option's help says so.
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Under: "platform"))
            .Should().ContainSingle().Which.Key.Scope.Should().Be(new AzureResourceScope(Mg, "rd-Reader-" + Mg));
    }

    [Fact]
    public async Task AGlobScopeReachesEverythingInEverySubscription()
    {
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Scope: "/subscriptions/*")).Should().HaveCount(4);
    }

    [Fact]
    public async Task AGlobScopeCanPickOutResourceGroupsByName()
    {
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Scope: "*/resourceGroups/*-rg"))
            .Select(r => r.DisplayName).Should().BeEquivalentTo("Owner", "Reader");
    }

    [Fact]
    public async Task PlainScopeTextStaysASubstringSearch()
    {
        using var t = await SeedAsync();
        RoleSelector.Filtered(t.Session, new RoleFilter(Scope: "prod")).Should().ContainSingle();
        RoleSelector.Filtered(t.Session, new RoleFilter(Scope: "Alpha")).Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveStillInsistsOnOneRolePerName()
    {
        using var t = await SeedAsync();
        var ambiguous = () => RoleSelector.Resolve(t.Session, ["Contributor"], new RoleFilter());
        ambiguous.Should().Throw<CliException>().Which.Message.Should().Contain("--all");
    }

    [Fact]
    public async Task ResolveAllTakesEveryRoleANameMatches()
    {
        using var t = await SeedAsync();
        RoleSelector.ResolveAll(t.Session, ["Contributor"], new RoleFilter()).Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveAllWithoutANameIsTheWholeFilteredList()
    {
        using var t = await SeedAsync();
        RoleSelector.ResolveAll(t.Session, [], new RoleFilter(Under: SubA)).Should().HaveCount(3);
    }

    [Fact]
    public async Task ResolveAllStillFailsOnANameThatMatchesNothing()
    {
        using var t = await SeedAsync();
        var missing = () => RoleSelector.ResolveAll(t.Session, ["Nonexistent"], new RoleFilter());
        missing.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.NotFound);
    }

    [Fact]
    public async Task UnderAndANameNarrowTogether()
    {
        // "Contributor across this subscription" — the shape the panel's subtree checkbox makes.
        using var t = await SeedAsync();
        RoleSelector.ResolveAll(t.Session, ["Contributor"], new RoleFilter(Under: SubA))
            .Should().ContainSingle().Which.Key.Scope.Should().BeOfType<AzureResourceScope>()
            .Which.Scope.Should().Be(SubA);
    }
}
