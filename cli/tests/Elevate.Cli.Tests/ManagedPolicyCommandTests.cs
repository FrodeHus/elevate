using System.Text.Json;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Session;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Discovery;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// What an organization's managed configuration permits, as the CLI applies it: sign-in methods
/// that may not be used, tenants that may not be tracked, and tenants that must be.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class ManagedPolicyCommandTests
{
    private const string FabrikamId = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string OtherId = "bbbbbbbb-0000-0000-0000-000000000003";
    private const string PinnedId = "cccccccc-0000-0000-0000-000000000004";

    private static ManagedConfiguration Managed(params (string Key, object? Value)[] pairs) =>
        ManagedConfiguration.Load(new DictionaryManagedSource(pairs.ToDictionary(p => p.Key, p => p.Value), "unit"));

    private static string Issuer(string tenantId)
        => $$"""{"issuer":"https://login.microsoftonline.com/{{tenantId}}/v2.0"}""";

    /// <summary>Runs the real command tree against the test session's directory, capturing both streams.</summary>
    private static async Task<(int Code, string Out, string Err)> RunAsync(TestSession session, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await Program.Main([.. args, "--no-color", "--data-dir", session.Directory]);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    // MARK: Sign-in methods

    [Fact]
    public async Task LoginWithAMethodTheOrganizationForbidsIsRefused()
    {
        using var session = new TestSession(Managed(("AllowedSignInMethods", new[] { "ownApp" })));
        var (code, _, error) = await RunAsync(session, "login", "--method", "cli");
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("The sign-in method 'cli' is not permitted by your organization. Allowed: own.");
    }

    [Fact]
    public async Task LoginWithoutAMethodUsesTheFirstAllowedOne()
    {
        // Only custom is allowed, so the default falls back to own and is refused by name: proof
        // that the default is taken from the allow-list rather than hard-coded.
        using var session = new TestSession(Managed(("AllowedSignInMethods", new[] { "custom" })));
        var (code, _, error) = await RunAsync(session, "login");
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("The sign-in method 'own' is not permitted by your organization. Allowed: custom.");

        ElevateSession.DefaultMethodName(Managed(("AllowedSignInMethods", new[] { "azureCLI", "azurePowerShell" })))
            .Should().Be("cli");
        ElevateSession.DefaultMethodName(ManagedConfiguration.None).Should().Be("own");
    }

    [Fact]
    public async Task AddingAnAccountWithAForbiddenMethodIsRefused()
    {
        using var t = new TestSession(Managed(("AllowedSignInMethods", new[] { "ownApp" })));
        var act = async () => await t.Session.AddAccountAsync(SignInMethod.AzureCLI);
        (await act.Should().ThrowAsync<CliException>())
            .Which.Message.Should().Be("The sign-in method 'cli' is not permitted by your organization. Allowed: own.");
    }

    [Fact]
    public async Task AccountsFlagsAMethodTheOrganizationNoLongerPermits()
    {
        using var session = new TestSession(Managed(("AllowedSignInMethods", new[] { "azureCLI" })));
        var (code, output, _) = await RunAsync(session, "accounts");
        code.Should().Be(ExitCodes.Ok);
        output.Should().Contain("not permitted");

        var (jsonCode, json, _) = await RunAsync(session, "accounts", "--json");
        jsonCode.Should().Be(ExitCodes.Ok);
        JsonDocument.Parse(json).RootElement[0].GetProperty("notPermitted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AccountsFlagsNothingWithoutAPolicy()
    {
        using var session = new TestSession();
        var (_, output, _) = await RunAsync(session, "accounts");
        output.Should().NotContain("not permitted");

        var (_, json, _) = await RunAsync(session, "accounts", "--json");
        JsonDocument.Parse(json).RootElement[0].GetProperty("notPermitted").GetBoolean().Should().BeFalse();
    }

    // MARK: Pinned tenants

    [Fact]
    public async Task TenantsRemoveRefusesAPinnedTenant()
    {
        using var session = Pinned();
        var (code, _, error) = await RunAsync(session, "tenants", "remove", "Fabrikam");
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("This tenant is pinned by your organization.");
        session.Store.Load().Tenants.Should().Contain(t => t.TenantId == PinnedId);
    }

    [Fact]
    public async Task TenantsListsThePinnedFlag()
    {
        using var session = Pinned();
        var (code, output, _) = await RunAsync(session, "tenants");
        code.Should().Be(ExitCodes.Ok);
        output.Should().Contain("pinned");

        var (_, json, _) = await RunAsync(session, "tenants", "--json");
        var rows = JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Where(e => e.GetProperty("tenantId").GetString() == PinnedId);
        rows.Should().ContainSingle().Which
            .GetProperty("flags").EnumerateArray().Select(f => f.GetString()).Should().Contain("pinned");
    }

    [Fact]
    public async Task PinnedTenantsAreTrackedForEveryAccount()
    {
        using var t = new TestSession(Managed(("PinnedTenants", new[] { PinnedId })));
        await t.Session.ResolveManagedTenantsAsync();

        t.Session.PinnedTenantIds.Should().Equal(PinnedId);
        var added = t.Session.Tenant(new TenantKey("id1", PinnedId));
        added.Should().NotBeNull();
        // No Graph name is reachable in tests, so the entry as configured is the label.
        added!.DisplayName.Should().Be(PinnedId);
        added.Source.Should().Be(TenantSource.Discovered);
        t.Store.Load().Tenants.Should().Contain(x => x.TenantId == PinnedId);
    }

    // MARK: Allowed tenants

    [Fact]
    public async Task AllowedTenantsResolveAndDropTheTenantsTheyExclude()
    {
        var http = new StubHttpClient();
        http.On("GET", "fabrikam.com/v2.0/.well-known/openid-configuration", Issuer(FabrikamId));
        using var t = new TestSession(Managed(("AllowedTenants", new[] { "fabrikam.com" })), http);
        t.Session.State.UpsertTenant(new TenantContext("id1", OtherId, "Other", TenantSource.Discovered));
        t.Session.Persist();

        await t.Session.ResolveManagedTenantsAsync();

        t.Session.AllowedTenantIds.Should().BeEquivalentTo([FabrikamId]);
        t.Session.ManagedTenantWarnings.Should().BeEmpty();
        t.Session.Tenants.Should().NotContain(x => x.TenantId == OtherId);
        // The home tenant stays even when it is not on the list: that is where the account lives.
        t.Session.Tenants.Should().Contain(x => x.TenantId == "t1");
        t.Session.ErrorLog.Entries.Select(e => e.Message)
            .Should().Contain("Removed tenants not permitted by your organization: Other");
        t.Store.Load().Tenants.Should().NotContain(x => x.TenantId == OtherId);
    }

    [Fact]
    public async Task AnUnresolvedAllowedEntryWarnsAndRestrictsNothing()
    {
        var http = new StubHttpClient();
        http.On("GET", "openid-configuration", string.Empty, status: 404);
        using var t = new TestSession(Managed(("AllowedTenants", new[] { "nope.example" }), ("PinnedTenants", new[] { "also-nope.example" })), http);
        t.Session.State.UpsertTenant(new TenantContext("id1", OtherId, "Other", TenantSource.Discovered));
        t.Session.Persist();

        await t.Session.ResolveManagedTenantsAsync();

        t.Session.AllowedTenantIds.Should().BeNull();
        t.Session.PinnedTenantIds.Should().BeEmpty();
        t.Session.ManagedTenantWarnings.Should().Equal(
            "AllowedTenants: could not resolve 'nope.example'",
            "PinnedTenants: could not resolve 'also-nope.example'");
        t.Session.Tenants.Should().Contain(x => x.TenantId == OtherId);
    }

    [Fact]
    public async Task AddingATenantTheOrganizationForbidsIsRefused()
    {
        var http = new StubHttpClient();
        http.On("GET", "fabrikam.com/v2.0/.well-known/openid-configuration", Issuer(FabrikamId));
        using var t = new TestSession(Managed(("AllowedTenants", new[] { "fabrikam.com" })), http);
        await t.Session.ResolveManagedTenantsAsync();

        var act = async () => await t.Session.AddTenantAsync(TestSession.Account, OtherId);
        var thrown = await act.Should().ThrowAsync<CliException>();
        thrown.Which.Message.Should().Be($"Tenant {OtherId} is not permitted by your organization.");
        thrown.Which.ExitCode.Should().Be(ExitCodes.Usage);

        // Discovery keeps the permitted ones and skips the rest.
        var added = t.Session.TrackTenants(TestSession.Account,
        [
            new DiscoveredTenant(FabrikamId, "Fabrikam", "fabrikam.com"),
            new DiscoveredTenant(OtherId, "Other", "other.example"),
        ]);
        added.Select(x => x.TenantId).Should().Equal(FabrikamId);
    }

    [Fact]
    public async Task NothingIsResolvedWithoutManagedTenants()
    {
        var http = new StubHttpClient();
        using var t = new TestSession(ManagedConfiguration.None, http);
        await t.Session.ResolveManagedTenantsAsync();

        http.Requests.Should().BeEmpty();
        t.Session.AllowedTenantIds.Should().BeNull();
        t.Session.PinnedTenantIds.Should().BeEmpty();
    }

    /// <summary>A session with one pinned tenant, already tracked so nothing goes to the network.</summary>
    private static TestSession Pinned()
    {
        var session = new TestSession(Managed(("PinnedTenants", new[] { PinnedId })));
        session.Session.State.UpsertTenant(new TenantContext("id1", PinnedId, "Fabrikam", TenantSource.Discovered));
        session.Session.Persist();
        return session;
    }
}
