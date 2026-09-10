using System.Text.Json;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Auth;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// The shared Elevate app from the CLI: <c>config set client-id shared</c>, how <c>config</c> and
/// <c>diagnostics</c> name it, and the <c>consent</c> link.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class SharedAppCommandTests
{
    private const string OwnClientId = "11111111-2222-3333-4444-555555555555";

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

    [Fact]
    public async Task SettingSharedResolvesToTheSharedClientIdAndStatesTheCaveatOnce()
    {
        using var session = new TestSession();
        // The test account signs in with the own-app method, so --yes skips the sign-out prompt.
        var (code, _, error) = await RunAsync(session, "config", "set", "client-id", "shared", "--yes");

        code.Should().Be(ExitCodes.Ok);
        new CliSettings(session.Directory).ClientId.Should().Be(SharedApp.ClientId);
        error.Should().Contain("no SLA").And.Contain("elevate consent");

        var (again, _, second) = await RunAsync(session, "config", "get", "client-id");
        again.Should().Be(ExitCodes.Ok);
        second.Should().NotContain("may change or be withdrawn", "the caveat is said when the id is set, not on every read");
    }

    [Fact]
    public async Task ConfigNamesTheSharedAppRatherThanOnlyTheGuid()
    {
        using var session = new TestSession();
        session.Settings.ClientId = SharedApp.ClientId;

        var (_, table, _) = await RunAsync(session, "config");
        table.Should().Contain("shared Elevate app");

        var (_, json, _) = await RunAsync(session, "config", "--json");
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("clientIdKind").GetString().Should().Be("shared");
        root.GetProperty("clientId").GetString().Should().Be(SharedApp.ClientId);

        session.Settings.ClientId = OwnClientId;
        var (_, own, _) = await RunAsync(session, "config", "--json");
        JsonDocument.Parse(own).RootElement.GetProperty("clientIdKind").GetString().Should().Be("own");
    }

    [Fact]
    public async Task ConsentUsesOrganizationsAndTheConsentPageForTheSharedApp()
    {
        using var session = new TestSession();
        session.Settings.ClientId = SharedApp.ClientId;

        var (code, output, _) = await RunAsync(session, "consent", "--json");

        code.Should().Be(ExitCodes.Ok);
        var root = JsonDocument.Parse(output).RootElement;
        root.GetProperty("tenant").GetString().Should().Be("organizations");
        root.GetProperty("clientIdKind").GetString().Should().Be("shared");
        var url = new Uri(root.GetProperty("url").GetString()!);
        url.AbsolutePath.Should().Be("/organizations/v2.0/adminconsent");
        url.Query.Should().Contain(Uri.EscapeDataString(SharedApp.ConsentRedirectUri))
            .And.Contain(Uri.EscapeDataString(SharedApp.ClientId));
    }

    [Fact]
    public async Task ConsentUsesNativeClientForAnOwnRegistrationAndResolvesATrackedTenant()
    {
        using var session = new TestSession();
        session.Settings.ClientId = OwnClientId;

        var (code, output, _) = await RunAsync(session, "consent", "--tenant", "Contoso", "--json");

        code.Should().Be(ExitCodes.Ok);
        var root = JsonDocument.Parse(output).RootElement;
        root.GetProperty("tenant").GetString().Should().Be(TestSession.Tenant.TenantId);
        root.GetProperty("clientIdKind").GetString().Should().Be("own");
        var url = new Uri(root.GetProperty("url").GetString()!);
        url.AbsolutePath.Should().Be($"/{TestSession.Tenant.TenantId}/v2.0/adminconsent");
        url.Query.Should().Contain(Uri.EscapeDataString(SharedApp.NativeClientRedirectUri)).And.NotContain("consent.html");
    }

    [Fact]
    public async Task ConsentTakesAGuidOrDomainAsTypedAndRefusesAnUnknownName()
    {
        using var session = new TestSession();
        session.Settings.ClientId = SharedApp.ClientId;

        var (_, byDomain, _) = await RunAsync(session, "consent", "--tenant", "fabrikam.com", "--json");
        JsonDocument.Parse(byDomain).RootElement.GetProperty("tenant").GetString().Should().Be("fabrikam.com");

        var (code, _, error) = await RunAsync(session, "consent", "--tenant", "nowhere");
        code.Should().Be(ExitCodes.NotFound);
        error.Should().Contain("No tracked tenant matches 'nowhere'");
    }

    [Fact]
    public async Task ConsentNeedsAClientId()
    {
        using var session = new TestSession();
        var (code, _, error) = await RunAsync(session, "consent");
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("config set client-id shared");
    }

    [Fact]
    public async Task DiagnosticsNamesTheKindOfClientIdNeverTheId()
    {
        using var session = new TestSession();
        // No accounts: with one, diagnostics would refresh it through the real token provider and
        // wait on a browser sign-in.
        session.Store.Save(new Elevate.Core.Storage.AppState());
        session.Settings.ClientId = SharedApp.ClientId;
        var (_, shared, _) = await RunAsync(session, "diagnostics");
        shared.Should().Contain("Client id: shared Elevate app").And.NotContain(SharedApp.ClientId);

        session.Settings.ClientId = OwnClientId;
        var (_, own, _) = await RunAsync(session, "diagnostics");
        own.Should().Contain("Client id: own registration").And.NotContain(OwnClientId);

        session.Settings.ClientId = string.Empty;
        var (_, unset, _) = await RunAsync(session, "diagnostics");
        unset.Should().Contain("Client id: not set");
    }
}
