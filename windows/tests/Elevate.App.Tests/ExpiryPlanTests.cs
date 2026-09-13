using Elevate.App.Notifications;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.App.Tests;

public class ExpiryPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly RoleKey Key = new("id1", "tenant1", new EntraDirectoryScope("role-def", "/"));

    private static ActiveAssignment Assignment(string id, DateTimeOffset? end, AssignmentStatus? status = null) =>
        new(Key, id, Now.AddHours(-1), end, status ?? AssignmentStatus.Active);

    [Fact]
    public void ForRoles_PlansLeadAndExpiredToastPerActiveAssignment()
    {
        var end = Now.AddHours(1);
        var planned = ExpiryPlan.ForRoles(
            [Assignment("a1", end), Assignment("p1", end, AssignmentStatus.PendingApproval), Assignment("n1", null)],
            new Dictionary<RoleKey, string> { [Key] = "Global Reader" },
            new Dictionary<TenantKey, string> { [Key.TenantKey] = "Contoso" });

        planned.Should().HaveCount(2);
        planned[0].Should().BeEquivalentTo(new PlannedToast(end - ExpiryPlan.LeadTime, "expiry-a1", "Global Reader expires in 5 minutes", "Contoso", "Extend", Key));
        planned[1].Should().BeEquivalentTo(new PlannedToast(end + ExpiryPlan.ExpiredDelay, "expired-a1", "Global Reader expired", "Contoso", "Activate again", Key));
        planned[0].Action.Should().Be("extend");
        planned[1].Action.Should().Be("again");
        planned.Should().OnlyContain(p => p.IsRoleToast);
    }

    [Fact]
    public void ForRoles_FallsBackToGenericNames()
    {
        var planned = ExpiryPlan.ForRoles([Assignment("a1", Now.AddHours(1))], new Dictionary<RoleKey, string>(), new Dictionary<TenantKey, string>());
        planned[0].Title.Should().Be("PIM role expires in 5 minutes");
        planned[0].Body.Should().Be("tenant1");
    }

    [Fact]
    public void ForPackages_PlansPlainToastAtEndPlusDelay()
    {
        var planned = ExpiryPlan.ForPackages([new PackageExpiry("pkg1", "Finance", "Contoso", Now.AddHours(2))]);
        planned.Should().ContainSingle();
        planned[0].Should().BeEquivalentTo(new PlannedToast(Now.AddHours(2) + ExpiryPlan.ExpiredDelay, "package-expired-pkg1", "Finance expired", "Access package in Contoso", string.Empty, null));
        planned[0].IsRoleToast.Should().BeFalse();
        planned[0].Action.Should().BeNull();
        ExpiryPlan.IsPackageTag(planned[0].Tag).Should().BeTrue();
        ExpiryPlan.IsRoleTag(planned[0].Tag).Should().BeFalse();
    }

    [Fact]
    public void Pending_DropsToastsAlreadyDueOrWithinOneSecond()
    {
        var planned = new[]
        {
            new PlannedToast(Now.AddSeconds(-10), "expiry-past", "t", "b", "Extend", Key),
            new PlannedToast(Now.AddMilliseconds(500), "expiry-soon", "t", "b", "Extend", Key),
            new PlannedToast(Now.AddSeconds(30), "expiry-later", "t", "b", "Extend", Key),
        };
        ExpiryPlan.Pending(planned, Now).Select(p => p.Tag).Should().Equal("expiry-later");
    }

    [Fact]
    public void Reconcile_KeepsMatchingRemovesStaleAndMovedAddsNew()
    {
        var t = Now.AddHours(1);
        var existing = new[]
        {
            new ScheduledEntry("expiry-keep", t),
            new ScheduledEntry("expiry-moved", t),
            new ScheduledEntry("expired-gone", t),
            new ScheduledEntry("package-expired-x", t),
            new ScheduledEntry("someone-else", t),
        };
        var wanted = new[]
        {
            new PlannedToast(t.AddMilliseconds(200), "expiry-keep", "t", "b", "Extend", Key),
            new PlannedToast(t.AddMinutes(30), "expiry-moved", "t", "b", "Extend", Key),
            new PlannedToast(t, "expiry-new", "t", "b", "Extend", Key),
        };

        var changes = ExpiryPlan.Reconcile(existing, wanted, ExpiryPlan.IsRoleTag);

        changes.Remove.Should().BeEquivalentTo(["expiry-moved", "expired-gone"]);
        changes.Add.Select(p => p.Tag).Should().BeEquivalentTo(["expiry-moved", "expiry-new"]);
    }

    [Fact]
    public void Reconcile_RemovesDuplicateEntriesForOneTag()
    {
        var t = Now.AddHours(1);
        var existing = new[] { new ScheduledEntry("expiry-a", t), new ScheduledEntry("expiry-a", t) };
        var wanted = new[] { new PlannedToast(t, "expiry-a", "t", "b", "Extend", Key) };

        var changes = ExpiryPlan.Reconcile(existing, wanted, ExpiryPlan.IsRoleTag);

        changes.Remove.Should().Equal("expiry-a");
        changes.Add.Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_WithNothingWantedClearsOnlyOwnedFamily()
    {
        var existing = new[] { new ScheduledEntry("package-expired-x", Now), new ScheduledEntry("expiry-a", Now) };

        var changes = ExpiryPlan.Reconcile(existing, [], ExpiryPlan.IsPackageTag);

        changes.Remove.Should().Equal("package-expired-x");
        changes.Add.Should().BeEmpty();
    }

    [Theory]
    [InlineData("extend")]
    [InlineData("again")]
    [InlineData("open")]
    public void RoleFor_RoundTripsTheKeyForActedOnActions(string action)
    {
        var arguments = new Dictionary<string, string> { ["action"] = action, ["key"] = ExpiryPlan.EncodeKey(Key) };
        ExpiryPlan.RoleFor(arguments).Should().Be(Key);
    }

    [Fact]
    public void RoleFor_IgnoresDismissMissingAndMalformedKeys()
    {
        ExpiryPlan.RoleFor(new Dictionary<string, string> { ["action"] = "dismiss", ["key"] = ExpiryPlan.EncodeKey(Key) }).Should().BeNull();
        ExpiryPlan.RoleFor(new Dictionary<string, string> { ["action"] = "extend" }).Should().BeNull();
        ExpiryPlan.RoleFor(new Dictionary<string, string> { ["action"] = "extend", ["key"] = "{not json" }).Should().BeNull();
        ExpiryPlan.RoleFor(new Dictionary<string, string>()).Should().BeNull();
    }
}
