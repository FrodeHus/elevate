using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.Core.Discovery;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.App.Tests;

/// <summary>
/// The managed configuration an organization pushes, applied to the app: the client id, the update
/// check, the sign-in methods, the tenants and the published profiles. Mirrors the macOS
/// <c>AppModelManagedTests</c> and the CLI's managed tests.
/// </summary>
public class ManagedSettingsTests
{
    private const string ManagedClientId = "22222222-2222-2222-2222-222222222222";
    private const string AllowedTenant = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenant = "33333333-3333-3333-3333-333333333333";

    /// <summary>A managed configuration built the way the real one is, through the key names.</summary>
    private static ManagedConfiguration Managed(params (ManagedKey Key, object? Value)[] values) =>
        ManagedConfiguration.Load(new DictionaryManagedSource(
            values.ToDictionary(v => v.Key.Name(), v => v.Value), "test policy"));

    // MARK: Client id

    [Fact]
    public void AManagedClientIdWinsOverTheStoredOneAndIgnoresWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "elevate-managed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stored = new AppSettings(directory, ManagedConfiguration.None)
            {
                ClientId = "44444444-4444-4444-4444-444444444444",
            };
            stored.ClientId.Should().Be("44444444-4444-4444-4444-444444444444");

            var settings = new AppSettings(directory, Managed((ManagedKey.ClientId, ManagedClientId)));
            settings.IsClientIdManaged.Should().BeTrue();
            settings.ClientId.Should().Be(ManagedClientId);
            settings.IsConfigured.Should().BeTrue();

            // A write is ignored while it is managed, and the user's own id is left untouched.
            settings.ClientId = "55555555-5555-5555-5555-555555555555";
            settings.ClientId.Should().Be(ManagedClientId);
            new AppSettings(directory, ManagedConfiguration.None).ClientId
                .Should().Be("44444444-4444-4444-4444-444444444444");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ApplyClientIdRefusesWhileTheClientIdIsManaged()
    {
        using var test = new TestModel(managed: Managed((ManagedKey.ClientId, ManagedClientId)));

        var act = () => test.Model.ApplyClientId("55555555-5555-5555-5555-555555555555");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("The client ID is managed by your organization");
        test.Settings.ClientId.Should().Be(ManagedClientId);
    }

    // MARK: Update check

    [Fact]
    public async Task TheUpdateCheckNeverRunsWhenTheOrganizationDisablesIt()
    {
        var http = new StubHttpClient();
        using var test = new TestModel(http: http, online: true, managed: Managed((ManagedKey.DisableUpdateCheck, true)));
        test.Settings.UpdateCheckDisabled.Should().BeTrue();

        await test.Model.CheckForUpdatesAsync(force: true);

        http.RequestsMatching("releases").Should().BeEmpty();
        test.Model.UpdateCheckMessage.Should().BeNull();
        test.Model.UpdateAvailable.Should().BeNull();
    }

    // MARK: Sign-in methods

    [Fact]
    public async Task ASignInMethodTheOrganizationWithholdsIsNeitherOfferedNorUsed()
    {
        using var test = new TestModel(managed: Managed((ManagedKey.AllowedSignInMethods, new[] { "ownApp" })));
        var model = test.Model;

        model.AvailableMethods.Select(m => m.Kind).Should().Equal(SignInMethodKind.OwnApp);
        model.IsMethodAllowed(SignInMethod.AzureCLI).Should().BeFalse();
        model.IsAvailable(SignInMethod.AzureCLI).Should().BeFalse();
        model.IsCustomMethodAllowed.Should().BeFalse();

        (await model.AddAccountAsync(SignInMethod.AzureCLI)).Should().BeFalse();

        model.Notice.Should().Be("That sign-in method is not permitted by your organization");
        model.Identities.Should().BeEmpty();
    }

    // MARK: Tenants

    [Fact]
    public async Task OnlyAllowedTenantsAreTracked()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(
            state, online: true, managed: Managed((ManagedKey.AllowedTenants, new[] { AllowedTenant })));
        var model = test.Model;

        model.AllowedTenantIds.Should().BeEquivalentTo([AllowedTenant]);
        model.IsTenantAllowed(AllowedTenant).Should().BeTrue();
        model.IsTenantAllowed(OtherTenant).Should().BeFalse();

        await model.TrackTenantsAsync(Sample.IdentityId,
        [
            new DiscoveredTenant(AllowedTenant, "Allowed", null),
            new DiscoveredTenant(OtherTenant, "Not allowed", null),
        ]);

        model.TenantsFor(Sample.IdentityId).Select(t => t.TenantId)
            .Should().Contain(AllowedTenant).And.NotContain(OtherTenant);
    }

    [Fact]
    public async Task APinnedTenantIsTrackedForEveryAccountAndCannotBeRemoved()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(
            state, online: true, managed: Managed((ManagedKey.PinnedTenants, new[] { AllowedTenant })));
        var model = test.Model;

        model.PinnedTenantIds.Should().Equal(AllowedTenant);
        var key = new TenantKey(Sample.IdentityId, AllowedTenant);
        model.Tenant(key).Should().NotBeNull();
        model.Tenant(key)!.Source.Should().Be(TenantSource.Discovered);
        model.IsPinnedTenant(key).Should().BeTrue();

        model.RemoveTenant(key);

        model.Tenant(key).Should().NotBeNull();
        model.Notice.Should().Be("This tenant is pinned by your organization");
    }

    [Fact]
    public async Task AManualAddIsRefusedForATenantTheOrganizationDoesNotPermit()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(
            state, online: true, managed: Managed((ManagedKey.AllowedTenants, new[] { AllowedTenant })));

        var act = () => test.Model.AddTenantAsync(Sample.IdentityId, OtherTenant);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"Tenant {OtherTenant} is not permitted by your organization");
    }

    // MARK: Published profiles

    /// <summary>One published profile, pinned, naming a role of the tenant the accounts are in.</summary>
    private const string Document = """
        {"version":1,"profiles":[{"id":"ops","name":"Ops","pinned":true,
          "roles":[{"kind":"entraDirectory","tenant":"11111111-1111-1111-1111-111111111111",
                    "role":"Security Reader"}]}]}
        """;

    [Fact]
    public async Task APublishedProfileIsListedButNeverChanged()
    {
        var identity = new Identity(Sample.IdentityId, "ops@example.com", "Ops", AllowedTenant, SignInMethod.OwnApp);
        var state = new AppState
        {
            Identities = [identity],
            Tenants = [new TenantContext(Sample.IdentityId, AllowedTenant, "Contoso", TenantSource.Home)],
        };
        using var test = new TestModel(state, online: true, managed: Managed((ManagedKey.ManagedProfiles, Document)));
        var model = test.Model;
        var tenantKey = new TenantKey(Sample.IdentityId, AllowedTenant);
        var roleKey = new RoleKey(Sample.IdentityId, AllowedTenant, new EntraDirectoryScope("role-def", "/"));
        model.State = state;
        model.Roles[tenantKey] = [new EligibleRole(roleKey, "Security Reader", RoleSource.Discovered, RolePolicy.ManualDefault)];
        await model.ResolveManagedTenantsAsync();

        var profile = model.Profiles.Should().ContainSingle().Subject;
        profile.Name.Should().Be("Ops");
        profile.Source.Should().Be(ProfileSource.Managed);
        profile.Entries.Select(e => e.RoleKey).Should().Equal(roleKey);
        model.IsManagedProfile(profile.Id).Should().BeTrue();
        model.ManagedProfileWarnings.Should().BeEmpty();

        // Pinned by the organization: shown as a chip, and it costs the user none of their own pins.
        model.PinnedProfiles.Select(p => p.Id).Should().Equal(profile.Id);
        model.CanPinAnotherProfile.Should().BeTrue();
        model.SetPinned(profile.Id, false).Should().BeFalse();

        model.DeleteProfile(profile.Id);
        model.RenameProfile(profile.Id, "Mine");
        model.RemoveProfileEntry(profile.Id, roleKey);

        model.Profile(profile.Id).Should().NotBeNull();
        model.Profile(profile.Id)!.Name.Should().Be("Ops");
        model.Profile(profile.Id)!.Entries.Should().ContainSingle();
        // Managed profiles are recomputed, never persisted.
        model.State.Profiles.Should().BeEmpty();
    }
}
