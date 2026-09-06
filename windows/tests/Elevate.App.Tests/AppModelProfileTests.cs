using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelProfileTests
{
    private static async Task<TestModel> ModelAsync(StubHttpClient? http = null)
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        var test = await TestModel.BootstrappedAsync(state, http: http);
        test.Model.Roles[Sample.TenantKey] =
        [
            Sample.Role(Sample.EntraKey, "Global Reader"),
            Sample.Role(Sample.AzureKey, "Owner"),
            Sample.Role(Sample.GroupKey, "Platform Admins"),
        ];
        return test;
    }

    [Fact]
    public async Task SaveProfileOrdersEntriesAndRemembersDurations()
    {
        using var test = await ModelAsync();
        var model = test.Model;
        model.State.Remember(Sample.GroupKey, "x", TimeSpan.FromMinutes(30));

        var profile = model.SaveProfile("  Ops  ", [Sample.GroupKey, Sample.AzureKey, Sample.EntraKey]);

        profile.Name.Should().Be("Ops");
        // Same account and tenant: ordered by kind (Entra, Azure, Group).
        profile.Entries.Select(e => e.RoleKey).Should().Equal(Sample.EntraKey, Sample.AzureKey, Sample.GroupKey);
        profile.Entries[2].LastDuration.Should().Be(TimeSpan.FromMinutes(30));
        profile.Entries[0].LastDuration.Should().BeNull();
        model.Profiles.Should().ContainSingle(p => p.Id == profile.Id);
        model.SaveProfile("   ", [Sample.EntraKey]).Name.Should().Be("Untitled profile");
    }

    [Fact]
    public async Task UpdateProfileKeepsRememberedDurationsOfSurvivingEntries()
    {
        using var test = await ModelAsync();
        var model = test.Model;
        var profile = model.SaveProfile("Ops", [Sample.EntraKey, Sample.AzureKey]);
        profile.Entries[0] = profile.Entries[0] with { LastDuration = TimeSpan.FromHours(2) };
        model.State.UpsertProfile(profile);

        model.UpdateProfile(profile.Id, [Sample.EntraKey, Sample.GroupKey]);

        var updated = model.Profile(profile.Id)!;
        updated.Entries.Select(e => e.RoleKey).Should().Equal(Sample.EntraKey, Sample.GroupKey);
        updated.Entries[0].LastDuration.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task RenameDeleteAndMove()
    {
        using var test = await ModelAsync();
        var model = test.Model;
        var a = model.SaveProfile("A", [Sample.EntraKey]);
        var b = model.SaveProfile("B", [Sample.AzureKey]);

        model.RenameProfile(a.Id, "  ");
        model.Profile(a.Id)!.Name.Should().Be("A");
        model.RenameProfile(a.Id, "Alpha");
        model.Profile(a.Id)!.Name.Should().Be("Alpha");

        model.MoveProfiles([1], 0);
        model.Profiles.Select(p => p.Id).Should().Equal(b.Id, a.Id);

        test.Settings.HotKey = new HotKeyBinding(HotKeyBinding.ModControl, 0x45, "Ctrl+E");
        test.Settings.HotKeyProfileId = a.Id;
        model.ApplyHotKey();
        test.HotKeys.Registered.Should().NotBeNull();

        model.DeleteProfile(a.Id);

        model.Profiles.Select(p => p.Id).Should().Equal(b.Id);
        test.Settings.HotKeyProfileId.Should().BeNull();
        test.HotKeys.Registered.Should().BeNull();
    }

    [Fact]
    public async Task BeginEditingReopensTheSelection()
    {
        using var test = await ModelAsync();
        var model = test.Model;
        var profile = model.SaveProfile("Ops", [Sample.EntraKey, Sample.GroupKey]);

        model.BeginEditing(profile.Id);

        model.SelectMode.Should().BeTrue();
        model.Selection.Should().BeEquivalentTo([Sample.EntraKey, Sample.GroupKey]);
        model.EditingProfileId.Should().Be(profile.Id);

        model.SelectMode = false;
        model.EditingProfileId.Should().BeNull();
        model.Selection.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanReportsDispositionsAndTreatsBusyTenantsAsNotLoaded()
    {
        using var test = await ModelAsync();
        var model = test.Model;
        var gone = Sample.Key(new EntraDirectoryScope("gone", "/"));
        var profile = model.SaveProfile("Ops", [Sample.EntraKey, Sample.AzureKey, gone]);
        model.Active[Sample.AzureKey] = Sample.Assignment(Sample.AzureKey);

        // The unknown role has no display name, so it sorts first among the Entra entries.
        profile.Entries.Select(e => e.RoleKey).Should().Equal(gone, Sample.EntraKey, Sample.AzureKey);

        var items = model.Plan(profile.Id);
        items.Select(i => i.Disposition).Should().Equal(
            ProfilePlanDisposition.NotEligible, ProfilePlanDisposition.Activate, ProfilePlanDisposition.AlreadyActive);

        model.Busy.Add(Sample.TenantKey);
        model.Plan(profile.Id).Select(i => i.Disposition).Should().Equal(
            ProfilePlanDisposition.NotLoaded, ProfilePlanDisposition.Activate, ProfilePlanDisposition.AlreadyActive);
        model.Plan(Guid.NewGuid()).Should().BeEmpty();
    }

    [Fact]
    public async Task RunProfileActivatesAndRemembersReasonAndDurations()
    {
        var http = new StubHttpClient();
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("POST", "roleAssignmentScheduleRequests", Fixtures.Text("entra-activate-response"), 201);
        using var test = await ModelAsync(http);
        var model = test.Model;
        var profile = model.SaveProfile("Ops", [Sample.EntraKey, Sample.AzureKey]);
        model.Active[Sample.AzureKey] = Sample.Assignment(Sample.AzureKey);
        var items = model.Plan(profile.Id);
        items = [.. items.Select(i => i.RoleKey == Sample.EntraKey ? i with { Duration = TimeSpan.FromMinutes(90) } : i)];

        var outcomes = await model.RunProfileAsync(profile.Id, items, "INC-9", null);

        outcomes.Should().ContainSingle().Which.Result.Should().BeOfType<ActivationResult.Activated>();
        var saved = model.Profile(profile.Id)!;
        saved.LastJustification.Should().Be("INC-9");
        saved.Entries.Single(e => e.RoleKey == Sample.EntraKey).LastDuration.Should().Be(TimeSpan.FromMinutes(90));
        // The already-active entry keeps its planned duration too; it was not skipped as ineligible.
        saved.Entries.Single(e => e.RoleKey == Sample.AzureKey).LastDuration.Should().Be(TimeSpan.FromHours(1));
        model.Remembered(Sample.EntraKey)!.Justification.Should().Be("INC-9");
        model.RunRequests.Should().BeEmpty();
        model.RequestRun(profile.Id);
        model.RunRequests[profile.Id].Should().Be(1);
    }

    [Fact]
    public async Task QuickRunNeedsTheDialogWithoutARememberedReasonAndRunsWithOne()
    {
        var http = new StubHttpClient();
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("POST", "roleAssignmentScheduleRequests", Fixtures.Text("entra-activate-response"), 201);
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(state, http: http, online: true);
        var model = test.Model;
        model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Global Reader")];
        var profile = model.SaveProfile("Ops", [Sample.EntraKey]);

        (await model.QuickRunAsync(profile.Id)).Should().BeFalse();
        (await model.QuickRunAsync(Guid.NewGuid())).Should().BeFalse();

        profile.LastJustification = "INC-1";
        model.State.UpsertProfile(profile);

        (await model.QuickRunAsync(profile.Id)).Should().BeTrue();

        model.Active.Should().ContainKey(Sample.EntraKey);
        test.Notifier.Posted.Should().ContainSingle().Which.Should().Be(("Ops", "Active for 02:00"));
    }

    [Fact]
    public async Task QuickActivateUsesTheRememberedReasonOrAsksForTheDialog()
    {
        var http = new StubHttpClient();
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("POST", "roleAssignmentScheduleRequests", Fixtures.Text("entra-activate-response"), 201);
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(state, http: http, online: true);
        var model = test.Model;
        model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Global Reader")];

        (await model.QuickActivateAsync(Sample.EntraKey)).Should().BeFalse();

        model.State.Remember(Sample.EntraKey, "INC-2", TimeSpan.FromHours(1));
        (await model.QuickActivateAsync(Sample.EntraKey)).Should().BeTrue();

        model.Active.Should().ContainKey(Sample.EntraKey);
        test.Notifier.Posted.Should().ContainSingle().Which.Title.Should().Be("Global Reader");
    }

    [Fact]
    public async Task QuickActivateOfflineIsHandledWithoutADialog()
    {
        using var test = await ModelAsync();
        (await test.Model.QuickActivateAsync(Sample.EntraKey)).Should().BeTrue();
        test.Notifier.Posted.Should().BeEmpty();
    }
}
