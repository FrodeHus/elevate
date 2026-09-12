using System.Text.Json;
using Elevate.App.Tests.Support;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

public class ProfileDeactivationTests
{
    private static ActiveAssignment Assignment(string role = "role-def", string schedule = "s1") => new(
        Sample.Key(new EntraDirectoryScope(role, "/")), "instance-" + schedule,
        DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddHours(1), AssignmentStatus.Active, schedule);

    private static string Instances(params ActiveAssignment[] assignments) => JsonSerializer.Serialize(new
    {
        value = assignments.Select(a => new
        {
            id = a.AssignmentId,
            roleDefinitionId = ((EntraDirectoryScope)a.RoleKey.Scope).RoleDefinitionId,
            directoryScopeId = "/", roleAssignmentScheduleId = a.ScheduleId,
            assignmentType = "Activated", startDateTime = a.StartDateTime, endDateTime = a.EndDateTime,
        }),
    });

    private static async Task<TestModel> ModelAsync(params ActiveAssignment[] assignments)
    {
        var http = new StubHttpClient();
        http.On("GET", "", """{"value":[]}""");
        http.On("GET", "/me", """{"id":"principal-1"}""");
        http.On("GET", "roleAssignmentScheduleInstances", Instances(assignments));
        http.On("POST", "roleAssignmentScheduleRequests", """{"id":"done","status":"Provisioned"}""", 201);
        return await TestModel.BootstrappedAsync(new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] }, http, online: true);
    }

    [Fact]
    public async Task ReplacedRunEntryIsTerminalWithoutSuccessOrWrite()
    {
        var original = Assignment();
        using var test = await ModelAsync(original with { ScheduleId = "replacement", AssignmentId = "replacement" });
        var run = new ProfileRun(Guid.NewGuid(), Guid.NewGuid(), "Ops", DateTimeOffset.UtcNow, [new(original, "Old")]);
        test.Model.State.ProfileRuns.Add(run);
        var result = await test.Model.DeactivateProfileRunAsync(run.Id);
        result[original.RoleKey].Should().Contain("skipped");
        run.Entries[0].Completed.Should().BeTrue();
        (await test.Model.DeactivateProfileRunAsync(run.Id)).Should().BeEmpty();
        test.Http.Requests.Where(r => r.Method == "POST").Should().BeEmpty();
        test.Model.DeactivationPhases.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingRunCannotDeactivateAnUnverifiableInterval()
    {
        var active = Assignment();
        var pending = active with { Status = AssignmentStatus.PendingApproval, EndDateTime = null };
        using var test = await ModelAsync(active);
        var run = new ProfileRun(Guid.NewGuid(), Guid.NewGuid(), "Ops", DateTimeOffset.UtcNow, [new(pending, "Pending")]);
        test.Model.State.ProfileRuns.Add(run);
        (await test.Model.DeactivateProfileRunAsync(run.Id))[active.RoleKey].Should().Contain("interval could not be verified");
        run.Entries[0].Completed.Should().BeFalse();
        test.Http.Requests.Where(r => r.Method == "POST").Should().BeEmpty();
    }

    [Fact]
    public async Task PreflightFailureDoesNotAbortOtherProviderGroups()
    {
        using var test = await ModelAsync();
        test.Model.Roles[Sample.TenantKey] = [Sample.Role(Sample.EntraKey, "Reader"), Sample.Role(Sample.GroupKey, "Group")];
        test.Http.On("GET", "roleAssignmentScheduleInstances", """{"error":{"code":"Forbidden","message":"Denied"}}""", 403);
        test.Http.On("GET", "privilegedAccess/group/eligibilityScheduleInstances", """{"value":[{"id":"elig","principalId":"principal-1","groupId":"group-1","accessId":"member"}]}""");
        test.Http.On("POST", "privilegedAccess/group/assignmentScheduleRequests", Fixtures.Text("group-activate-response"), 201);
        var profile = test.Model.SaveProfile("Ops", [Sample.EntraKey, Sample.GroupKey])!;
        var outcomes = await test.Model.RunProfileAsync(profile.Id, test.Model.Plan(profile.Id), "reason", null);
        outcomes.Should().HaveCount(2);
        outcomes.Single(o => o.RoleKey == Sample.EntraKey).Result.Should().BeOfType<Elevate.Core.Coordination.ActivationResult.Failed>();
        outcomes.Single(o => o.RoleKey == Sample.GroupKey).Result.Should().BeOfType<Elevate.Core.Coordination.ActivationResult.Activated>();
        test.Model.State.ProfileRuns.Should().ContainSingle().Which.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task FreshReplacementIsSkippedEvenWhenCacheStillContainsOriginal()
    {
        var original = Assignment();
        var replacement = original with { AssignmentId = "replacement", ScheduleId = "s2" };
        using var test = await ModelAsync(replacement);
        test.Model.Active[original.RoleKey] = original;
        (await test.Model.DeactivateAssignmentAsync(original)).Should().Contain("No matching");
        test.Http.Requests.Where(r => r.Method == "POST").Should().BeEmpty();
        test.Model.Active[original.RoleKey].Should().Be(replacement);
        test.Model.DeactivationPhases.Should().BeEmpty();
    }

    [Fact]
    public async Task RestrictedRunDeactivatesOnlyChosenRolesAndLeavesOthersRetryable()
    {
        var first = Assignment();
        var second = Assignment("other-role", "s2");
        using var test = await ModelAsync(first, second);
        var model = test.Model;
        var run = new ProfileRun(Guid.NewGuid(), Guid.NewGuid(), "Ops", DateTimeOffset.UtcNow,
            [new(first, "First"), new(second, "Second")]);
        model.State.ProfileRuns.Add(run);
        var results = await model.DeactivateProfileRunAsync(run.Id, only: new HashSet<RoleKey> { first.RoleKey });
        results.Keys.Should().ContainSingle().Which.Should().Be(first.RoleKey);
        results[first.RoleKey].Should().BeNull();
        run.Entries.Select(e => e.Completed).Should().Equal(true, false);
        test.Http.Requests.Count(r => r.Method == "POST").Should().Be(1);
        // The omitted role stays available to a later, unrestricted pass.
        model.ProfileRuns(run.ProfileId).Should().ContainSingle();
    }

    [Fact]
    public async Task MixedRunRetriesOnlyFailuresAndPersistsSuccessBeforeReturning()
    {
        var first = Assignment();
        var second = Assignment("other-role", "s2");
        using var test = await ModelAsync(first, second);
        var model = test.Model;
        var run = new ProfileRun(Guid.NewGuid(), Guid.NewGuid(), "Ops", DateTimeOffset.UtcNow,
            [new(first, "First"), new(second, "Second")]);
        model.State.ProfileRuns.Add(run);
        test.Http.On("POST", "roleAssignmentScheduleRequests", request =>
        {
            var body = System.Text.Encoding.UTF8.GetString(request.Body!);
            var response = body.Contains("other-role", StringComparison.Ordinal)
                ? """{"id":"waiting","status":"PendingApproval"}""" : """{"id":"done","status":"Provisioned"}""";
            return new Elevate.Core.Networking.HttpResponseData(201, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes(response));
        });
        var results = await model.DeactivateProfileRunAsync(run.Id);
        results[first.RoleKey].Should().BeNull();
        results[second.RoleKey].Should().Contain("not completed");
        await model.SavesSettledAsync();
        test.Store.Load().ProfileRuns[0].Entries.Select(e => e.Completed).Should().Equal(true, false);
        test.Http.On("POST", "roleAssignmentScheduleRequests", """{"id":"done","status":"Provisioned"}""", 201);
        var retry = await model.DeactivateProfileRunAsync(run.Id);
        retry.Keys.Should().ContainSingle().Which.Should().Be(second.RoleKey);
        test.Http.Requests.Count(r => r.Method == "POST").Should().Be(3);
        model.State.ProfileRuns[0].Entries.Should().OnlyContain(e => e.Completed);
    }

    [Fact]
    public async Task MinimumPeriodAndPendingRunsRemainRetryableWithoutPosting()
    {
        var young = Assignment() with { StartDateTime = DateTimeOffset.UtcNow.AddMinutes(-1) };
        using var test = await ModelAsync(young);
        var run = new ProfileRun(Guid.NewGuid(), Guid.NewGuid(), "Ops", DateTimeOffset.UtcNow,
            [new(young, "Young"), new(Assignment("pending", "pending") with { Status = AssignmentStatus.PendingApproval }, "Pending")]);
        test.Model.State.ProfileRuns.Add(run);
        var results = await test.Model.DeactivateProfileRunAsync(run.Id);
        results[young.RoleKey].Should().Contain("5-minute minimum");
        run.Entries.Should().OnlyContain(e => !e.Completed);
        test.Http.Requests.Where(r => r.Method == "POST").Should().BeEmpty();
    }

    [Fact]
    public async Task ExpiredScheduledRunIsTerminalWithoutPosting()
    {
        var expired = Assignment() with
        {
            Status = AssignmentStatus.Scheduled,
            StartDateTime = DateTimeOffset.UtcNow.AddHours(-2),
            EndDateTime = DateTimeOffset.UtcNow.AddHours(-1),
        };
        using var test = await ModelAsync();
        var run = new ProfileRun(Guid.NewGuid(), Guid.NewGuid(), "Ops", DateTimeOffset.UtcNow,
            [new(expired, "Expired schedule")]);
        test.Model.State.ProfileRuns.Add(run);

        var result = await test.Model.DeactivateProfileRunAsync(run.Id);

        result[expired.RoleKey].Should().Contain("Expired");
        run.Entries[0].Completed.Should().BeTrue();
        (await test.Model.DeactivateProfileRunAsync(run.Id)).Should().BeEmpty();
        test.Http.Requests.Where(r => r.Method == "POST").Should().BeEmpty();
    }

    [Fact]
    public async Task FreshPreexistingAssignmentIsSkippedByProfileActivation()
    {
        var assignment = Assignment();
        using var test = await ModelAsync(assignment);
        var model = test.Model;
        model.Roles[Sample.TenantKey] = [Sample.Role(assignment.RoleKey, "Reader")];
        model.Active.Clear();
        var profile = model.SaveProfile("Ops", [assignment.RoleKey])!;
        var plan = model.Plan(profile.Id);
        (await model.RunProfileAsync(profile.Id, plan, "reason", null)).Should().BeEmpty();
        test.Http.Requests.Where(r => r.Method == "POST").Should().BeEmpty();
    }
}
