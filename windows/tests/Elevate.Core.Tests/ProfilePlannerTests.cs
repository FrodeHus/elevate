using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>ProfilePlannerTests</c>.</summary>
public class ProfilePlannerTests
{
    private static readonly RoleKey K1 = new("i", "t", new EntraDirectoryScope("r1", "/"));
    private static readonly RoleKey K2 = new("i", "t", new EntraDirectoryScope("r2", "/"));
    private static readonly RoleKey K3 = new("i", "t", new GroupScope("g", GroupAccess.Member));
    private static readonly RoleKey K4 = new("i", "t", new EntraDirectoryScope("gone", "/"));

    private static readonly RolePolicy Policy = new(
        TimeSpan.FromHours(1), TimeSpan.FromHours(4), RequiresJustification: true, RequiresTicket: false,
        RequiresMfa: false, RequiresApproval: false);

    private static readonly IReadOnlySet<TenantKey> Loaded = new HashSet<TenantKey> { new("i", "t") };
    private static readonly IReadOnlySet<TenantKey> Nothing = new HashSet<TenantKey>();
    private static readonly Dictionary<RoleKey, ActiveAssignment> NoActive = [];
    private static readonly Dictionary<RoleKey, RoleMemory> NoMemory = [];

    private static EligibleRole Role(RoleKey k) => new(k, "R", RoleSource.Discovered, Policy);

    private static ActiveAssignment Assignment(RoleKey k, AssignmentStatus s) =>
        new(k, "a", DateTimeOffset.UtcNow, null, s);

    private static ActivationProfile Profile(params ActivationProfile.Entry[] entries) => new("p", entries);

    [Fact]
    public void DurationPrecedenceAndCap()
    {
        var profile = Profile(
            new(K1, TimeSpan.FromHours(8)),   // capped to the 4 h maximum
            new(K2),                          // falls to memory
            new(K3));                         // falls to policy default
        var roles = new Dictionary<RoleKey, EligibleRole> { [K1] = Role(K1), [K2] = Role(K2), [K3] = Role(K3) };
        var memory = new Dictionary<RoleKey, RoleMemory> { [K2] = new(K2, "x", TimeSpan.FromMinutes(30)) };

        var items = ProfilePlanner.Plan(profile, roles, NoActive, memory, Loaded);

        items.Select(i => i.Duration).Should().Equal(TimeSpan.FromHours(4), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1));
        items.Should().OnlyContain(i => i.Disposition == ProfilePlanDisposition.Activate);
        items.Select(i => i.RoleKey).Should().Equal(K1, K2, K3);   // profile order preserved
    }

    [Fact]
    public void Dispositions()
    {
        var profile = Profile(new(K1), new(K2), new(K3), new(K4));
        var roles = new Dictionary<RoleKey, EligibleRole> { [K1] = Role(K1), [K2] = Role(K2), [K3] = Role(K3) };
        var active = new Dictionary<RoleKey, ActiveAssignment>
        {
            [K1] = Assignment(K1, AssignmentStatus.Active),
            [K2] = Assignment(K2, AssignmentStatus.PendingApproval),
            [K3] = Assignment(K3, AssignmentStatus.PendingProvisioning),
        };

        var items = ProfilePlanner.Plan(profile, roles, active, NoMemory, Loaded);

        items.Select(i => i.Disposition).Should().Equal(
            ProfilePlanDisposition.AlreadyActive, ProfilePlanDisposition.Pending,
            ProfilePlanDisposition.Pending, ProfilePlanDisposition.NotEligible);
        items[3].Role.Should().BeNull();
        items[3].Duration.Should().Be(RolePolicy.ManualDefault.DefaultDuration);
    }

    [Fact]
    public void UnloadedTenantIsNotLoadedRatherThanNotEligible()
    {
        var profile = Profile(new ActivationProfile.Entry(K1));
        var roles = new Dictionary<RoleKey, EligibleRole>();

        var items = ProfilePlanner.Plan(profile, roles, NoActive, NoMemory, Nothing);

        items.Select(i => i.Disposition).Should().Equal(ProfilePlanDisposition.NotLoaded);
        // The duration still resolves, so it survives a run untouched.
        items[0].Duration.Should().Be(RolePolicy.ManualDefault.DefaultDuration);

        var eligible = ProfilePlanner.Plan(profile, roles, NoActive, NoMemory, Loaded);
        eligible.Select(i => i.Disposition).Should().Equal(ProfilePlanDisposition.NotEligible);
    }

    [Fact]
    public void FailedAssignmentPlansAsActivate()
    {
        var profile = Profile(new ActivationProfile.Entry(K1));
        var roles = new Dictionary<RoleKey, EligibleRole> { [K1] = Role(K1) };
        var active = new Dictionary<RoleKey, ActiveAssignment> { [K1] = Assignment(K1, AssignmentStatus.Failed("boom")) };

        var items = ProfilePlanner.Plan(profile, roles, active, NoMemory, Loaded);

        items.Select(i => i.Disposition).Should().Equal(ProfilePlanDisposition.Activate);
    }

    [Fact]
    public void ScheduledAssignmentPlansAsPending()
    {
        var profile = Profile(new ActivationProfile.Entry(K1));
        var roles = new Dictionary<RoleKey, EligibleRole> { [K1] = Role(K1) };
        var active = new Dictionary<RoleKey, ActiveAssignment> { [K1] = Assignment(K1, AssignmentStatus.Scheduled) };

        var items = ProfilePlanner.Plan(profile, roles, active, NoMemory, Loaded);

        items.Select(i => i.Disposition).Should().Equal(ProfilePlanDisposition.Pending);
    }
}
