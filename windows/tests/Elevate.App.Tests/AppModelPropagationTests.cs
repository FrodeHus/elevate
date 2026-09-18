using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>
/// The panel's side of #181: a row is not "ready" because PIM says active. Port of the macOS
/// <c>AppModelPropagationTests</c>.
/// </summary>
public class AppModelPropagationTests
{
    private const string GlobalAdmin = "62e90394-69f5-4237-9190-012177145e10";

    private static RoleKey EntraKey => Sample.Key(new EntraDirectoryScope(GlobalAdmin, "/"));

    private static AppState State() => new() { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };

    private static ActivationOutcome Activated(RoleKey key, DateTimeOffset? started = null) =>
        new(key, new ActivationResult.Activated(new ActiveAssignment(
            key, "a1", started ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), AssignmentStatus.Active)));

    /// <summary>A model whose probe answers from <paramref name="token"/>'s claims, paced for a test.</summary>
    private static async Task<TestModel> ModelAsync(string? token, RecordingNotifier? notifier = null)
    {
        var tokens = new FakeTokenProvider { RefreshedToken = token };
        var test = await TestModel.BootstrappedAsync(State(), online: true, tokens: tokens, notifier: notifier);
        test.Model.PropagationFirstInterval = TimeSpan.FromMilliseconds(1);
        test.Model.PropagationMaxInterval = TimeSpan.FromMilliseconds(1);
        return test;
    }

    /// <summary>Waits for the watch on <paramref name="key"/> to settle, rather than for a fixed time.</summary>
    private static async Task SettledAsync(AppModel model, RoleKey key)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (model.Propagation.TryGetValue(key, out var state) && state == PropagationState.Propagating)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"The probe on {key.Scope.Kind} never settled.");
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task ARowIsPropagatingTheMomentItIsActivated()
    {
        // No token to read, so the probe cannot confirm — but the row must say so before it asks.
        using var test = await ModelAsync(token: null);
        var key = EntraKey;
        test.Model.Active[key] = new ActiveAssignment(key, "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        test.Model.WatchPropagation([Activated(key)]);

        test.Model.Propagation[key].Should().Be(PropagationState.Propagating);
        test.Model.PropagationNote(key).Should().Be("propagating (~3 min)");
        test.Model.PropagationTooltip(key).Should().Contain("checking");
    }

    [Fact]
    public async Task AConfirmedProbeClearsTheRowAndNotifies()
    {
        var notifier = new RecordingNotifier();
        using var test = await ModelAsync(Jwt.WithRoles(GlobalAdmin), notifier);
        var key = EntraKey;
        // Started well before now, so it is not the "nobody waited for this" case.
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        test.Model.Active[key] = new ActiveAssignment(key, "a1", started, null, AssignmentStatus.Active);

        test.Model.WatchPropagation([Activated(key, started)]);
        await SettledAsync(test.Model, key);

        test.Model.Propagation[key].Should().Be(PropagationState.Ready);
        // Ready is recorded but says nothing on the row: the row is simply active again.
        test.Model.PropagationNote(key).Should().BeNull();
        notifier.Posted.Should().ContainSingle().Which.Title.Should().Contain("is ready");
    }

    [Fact]
    public async Task ARoleReadyStraightAwayIsNotWorthANotification()
    {
        var notifier = new RecordingNotifier();
        using var test = await ModelAsync(Jwt.WithRoles(GlobalAdmin), notifier);
        var key = EntraKey;
        test.Model.Active[key] = new ActiveAssignment(key, "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        test.Model.WatchPropagation([Activated(key)]);
        await SettledAsync(test.Model, key);

        test.Model.Propagation[key].Should().Be(PropagationState.Ready);
        notifier.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnobservableRoleGoesBackToAPlainActiveRow()
    {
        // A role scoped to an administrative unit is not carried in a token, so there is nothing to say.
        using var test = await ModelAsync(Jwt.WithRoles());
        var key = Sample.Key(new EntraDirectoryScope(GlobalAdmin, "/administrativeUnits/au1"));
        test.Model.Active[key] = new ActiveAssignment(key, "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

        test.Model.WatchPropagation([Activated(key)]);
        await SettledAsync(test.Model, key);

        test.Model.Propagation[key].Should().Be(PropagationState.Unobservable);
        test.Model.PropagationNote(key).Should().BeNull();
    }

    [Fact]
    public async Task DeactivatingWhileItPropagatesDropsTheWatch()
    {
        using var test = await ModelAsync(token: null);
        var key = EntraKey;
        test.Model.Active[key] = new ActiveAssignment(key, "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);
        test.Model.WatchPropagation([Activated(key)]);

        test.Model.StopWatchingPropagation(key);

        test.Model.Propagation.Should().NotContainKey(key);
    }

    [Fact]
    public async Task OnlyActivatedOutcomesAreWatched()
    {
        using var test = await ModelAsync(token: null);
        var key = EntraKey;
        var pending = new ActiveAssignment(key, "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.PendingApproval);

        test.Model.WatchPropagation([new ActivationOutcome(key, new ActivationResult.PendingApproval(pending))]);

        test.Model.Propagation.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnconfirmedRoleIsAnnouncedRatherThanLeftOnTheRow()
    {
        // Whoever activated it has moved on; the row alone would leave them trusting a dead role.
        var notifier = new RecordingNotifier();
        using var test = await ModelAsync(token: null, notifier);
        var key = EntraKey;
        test.Model.Active[key] = new ActiveAssignment(key, "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);
        test.Model.Propagation[key] = PropagationState.Propagating;

        test.Model.Settle(test.Model.ConfigGeneration, new PropagationOutcome(
            key, PropagationState.Unconfirmed, PropagationHints.LikelyCause(RoleScopeKind.EntraDirectory)));

        var posted = notifier.Posted.Should().ContainSingle().Subject;
        posted.Title.Should().Contain("not in effect");
        posted.Body.Should().Be(PropagationHints.LikelyCause(RoleScopeKind.EntraDirectory));
    }

    [Fact]
    public async Task SettledInEffectIsTrueOnlyWhenEveryRoleIsConfirmed()
    {
        using var test = await ModelAsync(token: null);
        var ready = EntraKey;
        var unobservable = Sample.Key(new GroupScope("grp-1", GroupAccess.Owner));
        var hold = TimeSpan.FromMilliseconds(200);

        test.Model.Propagation[ready] = PropagationState.Ready;
        (await test.Model.SettledInEffectAsync([ready], hold)).Should().BeTrue();

        test.Model.Propagation[unobservable] = PropagationState.Unobservable;
        (await test.Model.SettledInEffectAsync([ready, unobservable], hold)).Should().BeFalse();

        // A role nobody probed is not evidence of anything either.
        (await test.Model.SettledInEffectAsync([Sample.Key(new EntraDirectoryScope("other", "/"))], hold))
            .Should().BeFalse();
        (await test.Model.SettledInEffectAsync([], hold)).Should().BeFalse();
    }

    [Fact]
    public async Task SettledInEffectGivesUpAtTheHoldRatherThanWaitingOut()
    {
        // The dialog closes on whatever the probe has by then; it never holds the user for minutes.
        using var test = await ModelAsync(token: null);
        var key = EntraKey;
        test.Model.Propagation[key] = PropagationState.Propagating;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var inEffect = await test.Model.SettledInEffectAsync([key], TimeSpan.FromMilliseconds(250));
        clock.Stop();

        inEffect.Should().BeFalse();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ARoleThatIsNotBeingWatchedIsNotPropagating()
    {
        // The zero value of PropagationState is Propagating, so a lookup that falls back to the
        // default would mark every plain active row as propagating.
        using var test = await ModelAsync(token: null);

        test.Model.Propagation.Should().BeEmpty();
        test.Model.PropagationNote(EntraKey).Should().BeNull();
        test.Model.PropagationTooltip(EntraKey).Should().BeNull();
    }

    [Fact]
    public async Task TheUnconfirmedNoteNamesWhatToDoAboutIt()
    {
        using var test = await ModelAsync(token: null);
        var key = EntraKey;
        test.Model.Propagation[key] = PropagationState.Unconfirmed;

        test.Model.PropagationNote(key).Should().Be("not in effect yet");
        test.Model.PropagationTooltip(key).Should().Be(PropagationHints.LikelyCause(RoleScopeKind.EntraDirectory));
    }

}
