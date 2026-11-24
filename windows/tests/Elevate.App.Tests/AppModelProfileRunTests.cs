using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelProfileRunTests
{
    private static async Task<(TestModel Test, Guid Id)> ModelAsync(Exception? activationThrows = null)
    {
        var http = new StubHttpClient();
        http.On("GET", "roleAssignmentSchedule", """{"value":[]}""");
        http.On("GET", "/me", """{"id":"principal-1"}""");
        if (activationThrows is not null)
        {
            http.On("POST", "roleAssignmentScheduleRequests", (_, _) => throw activationThrows);
        }
        else
        {
            http.On("POST", "roleAssignmentScheduleRequests", Fixtures.Text("entra-activate-response"), 201);
        }

        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var test = await TestModel.BootstrappedAsync(state, http: http, online: true);
        test.Model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Global Reader")];
        var profile = test.Model.SaveProfile("Ops", [Sample.EntraKey])!;
        profile.LastJustification = "INC-1";
        test.Model.State.UpsertProfile(profile);
        // A panel open inside the refresh window re-reads nothing.
        test.Model.LastRefresh = DateTimeOffset.UtcNow;
        return (test, profile.Id);
    }

    [Fact]
    public async Task ASessionPlansTheProfileAndPrefillsTheReason()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var session = test.Model.RunSession(id);
        session.Justification.Should().Be("INC-1");
        session.ToActivate.Select(i => i.RoleKey).Should().Equal(Sample.EntraKey);
        test.Model.RunSession(id).Should().BeSameAs(session);
        session.CanSubmit(DateTimeOffset.Now).Should().BeTrue();
    }

    [Fact]
    public async Task UncheckingAnEntryRemovesItFromToActivate()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var session = test.Model.RunSession(id);
        session.Excluded.Add(Sample.EntraKey);
        session.ToActivate.Should().BeEmpty();
        session.CanSubmit(DateTimeOffset.Now).Should().BeFalse();
    }

    [Fact]
    public async Task LeavingAnUnstartedDraftDropsIt()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        test.Model.RunSessions.Should().ContainKey(id);
        test.Model.Select(new PanelSelection.AllTenants());
        test.Model.RunSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ADraftSurvivesThePanelClosing()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        test.Model.RunSession(id).Justification = "edited";
        test.Model.PanelClosed();
        test.Model.PanelOpened();
        test.Model.SelectedProfile.Should().Be(id);
        test.Model.RunSession(id).Justification.Should().Be("edited");
    }

    [Fact]
    public async Task ARunningSessionSurvivesLeavingAndReopens()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        test.Model.RunSession(id).Running = true;
        test.Model.Select(new PanelSelection.AllTenants());
        test.Model.RunSessions.Should().ContainKey(id);
        test.Model.PanelOpened();
        test.Model.SelectedProfile.Should().Be(id);
    }

    [Fact]
    public async Task UnseenResultsReopenOnTheProfile()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        test.Model.RunSession(id).Finished = true;
        test.Model.PanelClosed();
        test.Model.PanelOpened();
        test.Model.SelectedProfile.Should().Be(id);
    }

    [Fact]
    public async Task SeenResultsDoNotReopen()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        test.Model.PanelOpened();
        test.Model.RunSession(id).Finished = true;
        test.Model.MarkRunSeen(id);
        test.Model.PanelClosed();
        test.Model.PanelOpened();
        test.Model.SelectedProfile.Should().BeNull();
        test.Model.RunSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task AFinishWhileThePanelIsHiddenIsNotMarkedSeenUntilItReopensOnTheProfile()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.PanelOpened();
        test.Model.ShowProfile(id);
        test.Model.PanelClosed();
        test.Model.RunSession(id).Finished = true;

        // The bound control redraws on every change even while hidden.
        test.Model.MarkRunSeen(id);
        test.Model.RunSession(id).ResultsSeen.Should().BeFalse();

        test.Model.PanelOpened();
        test.Model.SelectedProfile.Should().Be(id);
        test.Model.RunSessions.Should().ContainKey(id);

        // Now on screen: the control's mark counts, and the next reopen goes Home.
        test.Model.MarkRunSeen(id);
        test.Model.RunSession(id).ResultsSeen.Should().BeTrue();
        test.Model.PanelClosed();
        test.Model.PanelOpened();
        test.Model.SelectedProfile.Should().BeNull();
        test.Model.RunSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunWindowMarksResultsSeenWhileThePanelIsClosed()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.RunWindowsOpen.Add(id);
        test.Model.RunSession(id).Finished = true;

        test.Model.MarkRunSeen(id);

        test.Model.RunSession(id).ResultsSeen.Should().BeTrue();
    }

    [Fact]
    public async Task SeenResultsInAnOpenRunWindowSurviveThePanelOpeningAndLeaving()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        test.Model.RunWindowsOpen.Add(id);
        await test.Model.SubmitRunAsync(id);
        test.Model.MarkRunSeen(id);
        var results = test.Model.RunSession(id).RowResults.Count;
        results.Should().BeGreaterThan(0);

        test.Model.PanelOpened();
        test.Model.RunSessions.Should().ContainKey(id);
        test.Model.RunSession(id).RowResults.Should().HaveCount(results);

        // The leave hook must not drop it either.
        test.Model.Select(new PanelSelection.AllTenants());
        test.Model.RunSessions.Should().ContainKey(id);

        // With the window gone the normal rule applies again.
        test.Model.Select(new PanelSelection.Profile(id));
        test.Model.RunWindowsOpen.Remove(id);
        test.Model.PanelOpened();
        test.Model.RunSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunThatEndsWithThePanelClosedNotifies()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        await test.Model.SubmitRunAsync(id);
        test.Model.RunSession(id).Finished.Should().BeTrue();
        test.Notifier.Posted.Should().ContainSingle().Which.Title.Should().Be("Ops");
    }

    [Fact]
    public async Task ARunThatEndsWithThePanelOpenDoesNotNotify()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.PanelOpened();
        test.Model.ShowProfile(id);
        await test.Model.SubmitRunAsync(id);
        test.Model.RunSession(id).Finished.Should().BeTrue();
        test.Notifier.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task ARunThatEndsWithItsRunWindowOpenDoesNotNotify()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.RunWindowsOpen.Add(id);
        await test.Model.SubmitRunAsync(id);
        test.Notifier.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletingTheShownProfileClearsSelectionAndSession()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        test.Model.RunSession(id).Running = true;
        test.Model.DeleteProfile(id);
        test.Model.SelectedProfile.Should().BeNull();
        test.Model.RunSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task ShowingAFinishedProfileAgainStartsAFreshPlan()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        test.Model.ShowProfile(id);
        var session = test.Model.RunSession(id);
        session.Excluded.Add(Sample.EntraKey);
        session.Finished = true;
        session.MutableRowResults[Sample.EntraKey] = new ActivationResult.Failed(new PimException(PimErrorKind.Unexpected, "x"));
        test.Model.ShowProfile(id);
        session.Finished.Should().BeFalse();
        session.Excluded.Should().BeEmpty();
        session.RowResults.Should().BeEmpty();
    }

    [Fact]
    public async Task AVanishedProfileDropsItsDraftButNotARunningSession()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var other = test.Model.SaveProfile("Other", [Sample.EntraKey])!.Id;
        test.Model.RunSession(id);
        test.Model.RunSession(other).Running = true;
        test.Model.State.RemoveProfile(id);
        test.Model.State.RemoveProfile(other);
        test.Model.PanelOpened();
        test.Model.RunSessions.Keys.Should().Equal(other);
    }

    [Fact]
    public async Task ARunningSessionKeepsItsRowResultsFromProgress()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var session = test.Model.RunSession(id);
        session.Running = true;
        var result = new ActivationResult.Failed(new PimException(PimErrorKind.Unexpected, "boom"));
        test.Model.Progress[Sample.EntraKey] = result;
        test.Model.Touch();
        session.RowResults[Sample.EntraKey].Should().Be(result);
    }

    [Fact]
    public async Task EveryChangeToTheRunStateNotifies()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var model = test.Model;
        var changes = 0;
        model.Changed += (_, _) => changes++;

        void Fires(string what, Action act)
        {
            var before = changes;
            act();
            changes.Should().BeGreaterThan(before, what);
        }

        model.ShowProfile(id);
        Fires("reset", () => model.ResetRunSession(id));
        Fires("panel open keeping the draft", () => model.PanelOpened());
        Fires("mark seen", () => model.MarkRunSeen(id));
        Fires("panel closed", () => model.PanelClosed());

        // The first notification of a submit must come before the activation finishes.
        var atStart = -1;
        model.Changed += (_, _) =>
        {
            if (atStart < 0 && model.RunSession(id).Running)
            {
                atStart = changes;
            }
        };
        await model.SubmitRunAsync(id);
        atStart.Should().BeGreaterThan(-1, "Running = true must be announced before the await");
    }

    public static TheoryData<Exception> Failures => new()
    {
        // Ordinary exceptions are turned into Failed outcomes inside the activation; only a
        // cancellation escapes it, which is the case that used to leave a session Running.
        new OperationCanceledException("timed out"),
        new TaskCanceledException("http timeout"),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ASubmitThatThrowsEndsFinishedWithTheFailureAndDropsOnLeaving(Exception failure)
    {
        var (test, id) = await ModelAsync(failure);
        using var _ = test;
        var model = test.Model;
        model.ShowProfile(id);
        var session = model.RunSession(id);
        var changes = 0;
        model.Changed += (_, _) => changes++;

        await model.SubmitRunAsync(id);

        session.Running.Should().BeFalse();
        session.Finished.Should().BeTrue();
        session.Failure.Should().NotBeNullOrEmpty();
        changes.Should().BeGreaterThan(0);
        model.Select(new PanelSelection.AllTenants());
        model.RunSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedRunPostsNoOutcomeToast()
    {
        var (test, id) = await ModelAsync(new OperationCanceledException("timed out"));
        using var _ = test;
        test.Model.ShowProfile(id);
        await test.Model.SubmitRunAsync(id);
        test.Model.RunSession(id).Failure.Should().NotBeNullOrEmpty();
        test.Notifier.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task ReopeningAfterBrowsingKeepsAnUnseenFinishedRun()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var model = test.Model;
        // What the bound control does on every redraw.
        model.Changed += (_, _) =>
        {
            if (model.RunSessions.TryGetValue(id, out var s) && s.Finished && !s.ResultsSeen)
            {
                model.MarkRunSeen(id);
            }
        };
        model.PanelOpened();
        model.ShowProfile(id);
        model.Browsing = true;
        model.PanelClosed();
        model.RunSession(id).Finished = true;

        model.PanelOpened();

        model.SelectedProfile.Should().Be(id);
        model.RunSessions.Should().ContainKey(id);
    }

    [Fact]
    public async Task AnUnstartedRunWindowLeavesNoSelectionOrSessionBehind()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var model = test.Model;
        model.RunSession(id);
        model.RunWindowsOpen.Add(id);

        model.RunWindowClosed(id);

        model.SelectedProfile.Should().BeNull();
        model.RunSessions.Should().BeEmpty();
        model.RunWindowsOpen.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnseenRunFromARunWindowSurvivesItsClosingAndReopensTheFlyout()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var model = test.Model;
        model.RunSession(id);
        model.RunWindowsOpen.Add(id);
        await model.SubmitRunAsync(id);

        model.RunWindowClosed(id);

        model.SelectedProfile.Should().BeNull();
        model.RunSessions.Should().ContainKey(id);
        model.PanelOpened();
        model.SelectedProfile.Should().Be(id);
    }

    [Fact]
    public async Task ARunWindowClosingKeepsTheFlyoutsOwnDraft()
    {
        var (test, id) = await ModelAsync();
        using var _ = test;
        var model = test.Model;
        model.ShowProfile(id);
        model.RunWindowsOpen.Add(id);

        model.RunWindowClosed(id);

        model.SelectedProfile.Should().Be(id);
        model.RunSessions.Should().ContainKey(id);
    }
}
