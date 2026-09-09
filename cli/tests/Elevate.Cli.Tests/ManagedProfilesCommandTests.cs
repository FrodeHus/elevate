using System.Text.Json;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Catalogue;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// Profiles an organization publishes (design §7): where they come from, that the CLI refuses to
/// change them, and <c>profiles export</c>, which turns a profile someone already built into the
/// document an administrator publishes.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class ManagedProfilesCommandTests
{
    private const string ContosoId = "11111111-0000-0000-0000-000000000001";
    private const string ProfilesUrl = "https://example.com/profiles.json";

    private static readonly TenantKey Contoso = new("id1", ContosoId);

    private static ManagedConfiguration Managed(params (string Key, object? Value)[] pairs) =>
        ManagedConfiguration.Load(new DictionaryManagedSource(pairs.ToDictionary(p => p.Key, p => p.Value), "unit"));

    /// <summary>The §7.1 document, with the name (and so the profile) the test wants.</summary>
    private static string Document(string name = "Prod incident", string id = "prod-incident") => $$"""
        {
          "version": 1,
          "profiles": [
            {
              "id": "{{id}}",
              "name": "{{name}}",
              "reason": "Incident response",
              "pinned": true,
              "roles": [
                { "kind": "entraDirectory", "tenant": "{{ContosoId}}", "role": "Security Reader" },
                { "kind": "group", "tenant": "{{ContosoId}}", "group": "SRE on-call", "access": "member", "duration": "PT4H" }
              ]
            }
          ]
        }
        """;

    /// <summary>A session with one published profile inline, and a tenant for it to resolve against.</summary>
    private static TestSession Published(string? document = null, IHttpClient? http = null, string? url = null)
    {
        (string, object?)[] pairs = url is null
            ? [("ManagedProfiles", document ?? Document())]
            : [("ManagedProfiles", document ?? Document()), ("ManagedProfilesUrl", url)];
        var session = new TestSession(Managed(pairs), http);
        session.Session.State.UpsertTenant(new TenantContext("id1", ContosoId, "Contoso", TenantSource.Discovered));
        session.Session.Persist();
        return session;
    }

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

    // MARK: Listing

    [Fact]
    public async Task ProfilesListsWhereEachProfileCameFrom()
    {
        using var t = Published();
        t.Session.SaveProfile("Mine", [TestSession.EntraKey("role-1")]);

        var (code, output, _) = await RunAsync(t, "profiles");
        code.Should().Be(ExitCodes.Ok);
        output.Should().Contain("Prod incident").And.Contain("managed").And.Contain("user");

        var (_, json, _) = await RunAsync(t, "profiles", "--json");
        var rows = JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
        rows.Single(r => r.GetProperty("name").GetString() == "Prod incident").GetProperty("source").GetString().Should().Be("managed");
        rows.Single(r => r.GetProperty("name").GetString() == "Mine").GetProperty("source").GetString().Should().Be("user");
    }

    [Fact]
    public async Task ShowNamesTheSource()
    {
        using var t = Published();
        var (code, output, _) = await RunAsync(t, "profiles", "show", "Prod incident");
        code.Should().Be(ExitCodes.Ok);
        output.Should().Contain("Source: managed");

        var (_, json, _) = await RunAsync(t, "profiles", "show", "Prod incident", "--json");
        JsonDocument.Parse(json).RootElement.GetProperty("source").GetString().Should().Be("managed");
    }

    // MARK: Refusals

    [Fact]
    public async Task RenameAndDeleteRefuseAPublishedProfile()
    {
        using var t = Published();
        var (rename, _, renameError) = await RunAsync(t, "profiles", "rename", "Prod incident", "Mine");
        rename.Should().Be(ExitCodes.Usage);
        renameError.Should().Contain("'Prod incident' is published by your organization and cannot be changed.");

        var (delete, _, deleteError) = await RunAsync(t, "profiles", "delete", "Prod incident", "--yes");
        delete.Should().Be(ExitCodes.Usage);
        deleteError.Should().Contain("'Prod incident' is published by your organization and cannot be changed.");

        // And it is still there afterwards.
        var (_, output, _) = await RunAsync(t, "profiles");
        output.Should().Contain("Prod incident");
    }

    [Fact]
    public async Task SavingOverAPublishedNameIsRefused()
    {
        using var t = Published();
        var (code, _, error) = await RunAsync(t, "profiles", "save", "prod incident", "Security Reader");
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("'Prod incident' is published by your organization and cannot be changed.");
        t.Store.Load().Profiles.Should().BeEmpty();
    }

    [Fact]
    public void TheSessionRefusesEveryChangeToAPublishedProfile()
    {
        using var t = Published();
        var managed = t.Session.Profiles.Single(p => p.Name == "Prod incident");
        t.Session.IsManagedProfile(managed.Id).Should().BeTrue();

        var rename = () => t.Session.RenameProfile(managed, "Other");
        rename.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
        var update = () => t.Session.UpdateProfile(managed, [TestSession.EntraKey("role-1")]);
        update.Should().Throw<CliException>();
        var delete = () => t.Session.DeleteProfile(managed.Id);
        delete.Should().Throw<CliException>();
        var save = () => t.Session.SaveProfile("  PROD INCIDENT ", [TestSession.EntraKey("role-1")]);
        save.Should().Throw<CliException>();

        // Published profiles are never written to state.json.
        t.Store.Load().Profiles.Should().BeEmpty();
    }

    // MARK: Running

    [Fact]
    public async Task RunPlansAPublishedProfile()
    {
        using var t = Published(http: new StubHttpClient());
        var (code, output, _) = await RunAsync(t, "profiles", "run", "Prod incident", "--dry-run");
        code.Should().Be(ExitCodes.Ok);
        output.Should().Contain("Plan").And.Contain("Security Reader");
        t.Store.Load().Profiles.Should().BeEmpty();
    }

    // MARK: Export

    [Fact]
    public async Task ExportPrintsADocumentThatParsesBack()
    {
        using var t = Published();
        var entra = new RoleKey("id1", ContosoId, new EntraDirectoryScope("62e90394-69f5-4237-9190-012177145e10", "/"));
        var azure = new RoleKey("id1", ContosoId, new AzureResourceScope("/subscriptions/s1", "b24988ac-6180-42a0-ab88-20f7382dd24c"));
        var group = new RoleKey("id1", ContosoId, new GroupScope("group-1", GroupAccess.Owner));
        t.Session.State.ManualRoles.Add(new ManualRole(Contoso, entra.Scope, "Global Administrator"));
        t.Session.State.ManualRoles.Add(new ManualRole(Contoso, azure.Scope, "Contributor"));
        t.Session.State.UpsertProfile(new ActivationProfile("Morning shift",
            [new(entra, TimeSpan.FromHours(2)), new(azure), new(group)], "Daily work"));
        t.Session.Persist();

        var (code, output, _) = await RunAsync(t, "profiles", "export", "Morning shift");
        code.Should().Be(ExitCodes.Ok);

        var set = ManagedProfileSet.Parse(output);
        var profile = set.Profiles.Should().ContainSingle().Subject;
        profile.Id.Should().Be("morning-shift");
        profile.Name.Should().Be("Morning shift");
        profile.Reason.Should().Be("Daily work");
        profile.Roles.Should().HaveCount(3);

        var entraSpec = profile.Roles.Single(r => r.Kind == RoleScopeKind.EntraDirectory);
        entraSpec.Tenant.Should().Be(ContosoId);
        entraSpec.Role.Should().Be("Global Administrator");
        entraSpec.DirectoryScope.Should().Be("/");
        entraSpec.Duration.Should().Be(TimeSpan.FromHours(2));

        var azureSpec = profile.Roles.Single(r => r.Kind == RoleScopeKind.AzureResource);
        azureSpec.Role.Should().Be("Contributor");
        azureSpec.Scope.Should().Be("/subscriptions/s1");
        azureSpec.Duration.Should().BeNull();

        // No role of that name is loaded, so the id from the key stands in.
        var groupSpec = profile.Roles.Single(r => r.Kind == RoleScopeKind.Group);
        groupSpec.Group.Should().Be("group-1");
        groupSpec.Access.Should().Be(GroupAccess.Owner);
    }

    [Fact]
    public async Task ExportRefusesAPublishedProfile()
    {
        using var t = Published();
        var (code, _, error) = await RunAsync(t, "profiles", "export", "Prod incident");
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("'Prod incident' is published by your organization; export its source instead.");
    }

    // MARK: Fetching

    [Fact]
    public async Task AFetchedDocumentIsCachedAndWinsOnId()
    {
        var http = new StubHttpClient();
        http.On("GET", "profiles.json", """
            {
              "version": 1,
              "profiles": [
                { "id": "prod-incident", "name": "Prod incident (published)", "roles": [] },
                { "id": "backup", "name": "Backup", "roles": [] }
              ]
            }
            """);
        using var t = Published(http: http, url: ProfilesUrl);

        await t.Session.RefreshManagedProfilesAsync(false);

        t.Session.ManagedProfiles.Select(p => p.Name).Should().Equal("Prod incident (published)", "Backup");
        t.Settings.ManagedProfilesFetchedAt.Should().NotBeNull();
        var cache = Path.Combine(t.Directory, "managed-profiles.json");
        File.Exists(cache).Should().BeTrue();
        ManagedProfileSet.Parse(File.ReadAllText(cache)).Profiles.Should().HaveCount(2);
    }

    [Fact]
    public async Task AFailedFetchKeepsTheCachedDocumentAndWarns()
    {
        var http = new StubHttpClient();
        http.On("GET", "profiles.json", string.Empty, status: 500);
        using var t = Published(http: http, url: ProfilesUrl);
        File.WriteAllText(Path.Combine(t.Directory, "managed-profiles.json"),
            """{ "version": 1, "profiles": [ { "id": "prod-incident", "name": "Prod incident (cached)", "roles": [] } ] }""");

        await t.Session.RefreshManagedProfilesAsync(false);

        t.Session.ManagedProfiles.Select(p => p.Name).Should().Equal("Prod incident (cached)");
        t.Session.ManagedProfileWarnings.Should().Contain("ManagedProfilesUrl: managed profiles fetch failed with HTTP 500");
        t.Settings.ManagedProfilesFetchedAt.Should().BeNull();
    }

    [Fact]
    public async Task TheDocumentIsFetchedAtMostOnceADay()
    {
        var http = new StubHttpClient();
        http.On("GET", "profiles.json", Document(name: "Prod incident (published)"));
        using var t = Published(http: http, url: ProfilesUrl);

        await t.Session.RefreshManagedProfilesAsync(false);
        await t.Session.RefreshManagedProfilesAsync(false);
        Fetches(http).Should().Be(1);

        // Asked to, it fetches anyway.
        await t.Session.RefreshManagedProfilesAsync(true);
        Fetches(http).Should().Be(2);
    }

    [Fact]
    public void ADocumentThatCannotBeParsedIsOneWarning()
    {
        using var t = new TestSession(Managed(("ManagedProfiles", "not a document")));
        t.Session.ManagedProfiles.Should().BeEmpty();
        t.Session.ManagedProfileWarnings.Should().Equal("ManagedProfiles: not a JSON object");
    }

    [Fact]
    public async Task NothingIsFetchedWithoutAUrl()
    {
        var http = new StubHttpClient();
        using var t = Published(http: http);
        await t.Session.RefreshManagedProfilesAsync(true);
        Fetches(http).Should().Be(0);
    }

    private static int Fetches(StubHttpClient http) =>
        http.Requests.Count(r => r.Url.AbsoluteUri.Contains("profiles.json", StringComparison.Ordinal));
}
