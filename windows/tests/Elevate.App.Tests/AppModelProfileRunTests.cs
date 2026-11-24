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
    private static async Task<(TestModel Test, Guid Id)> ModelAsync()
    {
        var http = new StubHttpClient();
        http.On("GET", "roleAssignmentSchedule", """{"value":[]}""");
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("POST", "roleAssignmentScheduleRequests", Fixtures.Text("entra-activate-response"), 201);
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
        test.Model.RunSession(id).Finished = true;
        test.Model.MarkRunSeen(id);
        test.Model.PanelClosed();
        test.Model.PanelOpened();
        test.Model.SelectedProfile.Should().BeNull();
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
}
