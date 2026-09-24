using Elevate.App.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>
/// Port of the macOS fix in #201. A background refresh that cannot read a tenant keeps its known
/// rows; the clock tick drops the ones whose end has passed, so an activation that ended while the
/// machine slept does not linger in Active now with a Deactivate that can only fail.
/// </summary>
public class AppModelLapsedAssignmentTests
{
    private static AppState StateWithTenant() => new()
    {
        Identities = [Sample.Identity()],
        Tenants = [Sample.Tenant()],
    };

    [Fact]
    public async Task DropsActiveRowsAMinutePastTheirEnd()
    {
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: false);
        var model = test.Model;
        var now = DateTimeOffset.UtcNow;
        model.Active[Sample.EntraKey] = Sample.Assignment(Sample.EntraKey, ends: now.AddMinutes(-2));
        model.Active[Sample.AzureKey] = Sample.Assignment(Sample.AzureKey, ends: now.AddHours(1));
        model.Active[Sample.GroupKey] = new ActiveAssignment(Sample.GroupKey, "p", now.AddHours(-3), null, AssignmentStatus.PendingApproval);

        await model.DropLapsedAssignmentsAsync(now);

        model.Active.Keys.Should().BeEquivalentTo([Sample.AzureKey, Sample.GroupKey]);
    }

    /// <summary>The "expired" toast is due five seconds after the end; dropping the row sooner would withdraw it.</summary>
    [Fact]
    public async Task KeepsARowThatEndedLessThanAMinuteAgo()
    {
        using var test = await TestModel.BootstrappedAsync(StateWithTenant(), online: false);
        var model = test.Model;
        var now = DateTimeOffset.UtcNow;
        model.Active[Sample.EntraKey] = Sample.Assignment(Sample.EntraKey, ends: now.AddSeconds(-30));

        await model.DropLapsedAssignmentsAsync(now);

        model.Active.Should().ContainKey(Sample.EntraKey);
    }
}
