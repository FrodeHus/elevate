using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>
/// The propagation probes: what each provider answers when a role is active but not yet usable.
/// Port of the Swift <c>EffectiveAccessTests</c>.
/// </summary>
public class EffectiveAccessTests
{
    private const string GlobalAdmin = "62e90394-69f5-4237-9190-012177145e10";
    private static readonly Identity TestIdentity = new("id1", "u@contoso.com", "U", "t-home");

    private static ActiveAssignment Assignment(RoleScope scope) =>
        new(new RoleKey("id1", "t1", scope), "a1", DateTimeOffset.UtcNow, null, AssignmentStatus.Active);

    // MARK: Entra directory roles

    [Fact]
    public async Task Entra_ConfirmsWhenAFreshTokenCarriesTheRole()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.WithRoles(GlobalAdmin) };
        var provider = new EntraDirectoryProvider(new StubHttpClient(), tokens);

        var access = await provider.EffectiveAccessAsync(Assignment(new EntraDirectoryScope(GlobalAdmin, "/")), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Confirmed);
        tokens.ForcedCalls.Should().ContainSingle().Which.Should().Be("t1");
    }

    [Fact]
    public async Task Entra_IsNotYetWhileTheRoleIsMissingFromTheToken()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.WithRoles("some-other-role") };
        var provider = new EntraDirectoryProvider(new StubHttpClient(), tokens);

        var access = await provider.EffectiveAccessAsync(Assignment(new EntraDirectoryScope(GlobalAdmin, "/")), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.NotYet);
    }

    [Fact]
    public async Task Entra_MatchesTheRoleIdWhateverItsCase()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.WithRoles(GlobalAdmin.ToUpperInvariant()) };
        var provider = new EntraDirectoryProvider(new StubHttpClient(), tokens);

        var access = await provider.EffectiveAccessAsync(Assignment(new EntraDirectoryScope(GlobalAdmin, "/")), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Confirmed);
    }

    [Fact]
    public async Task Entra_CannotTellForARoleScopedToAnAdministrativeUnit()
    {
        // wids carries tenant-wide roles only, so the role's absence would prove nothing.
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.WithRoles() };
        var provider = new EntraDirectoryProvider(new StubHttpClient(), tokens);

        var access = await provider.EffectiveAccessAsync(
            Assignment(new EntraDirectoryScope(GlobalAdmin, "/administrativeUnits/au1")), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Unknown);
        tokens.ForcedCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Entra_CannotTellWhenTheProviderWillNotMintAFreshToken()
    {
        var tokens = new FakeTokenProvider { CanForceRefresh = false, RefreshedToken = Jwt.WithRoles(GlobalAdmin) };
        var provider = new EntraDirectoryProvider(new StubHttpClient(), tokens);

        var access = await provider.EffectiveAccessAsync(Assignment(new EntraDirectoryScope(GlobalAdmin, "/")), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Unknown);
        access.Detail.Should().Contain("fresh token");
    }

    [Fact]
    public async Task Entra_CannotTellWhenTheTokenIsOpaque()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = "opaque-token" };
        var provider = new EntraDirectoryProvider(new StubHttpClient(), tokens);

        var access = await provider.EffectiveAccessAsync(Assignment(new EntraDirectoryScope(GlobalAdmin, "/")), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Unknown);
    }

    // MARK: Group membership

    private static byte[] CheckMemberObjects(params string[] ids) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["value"] = new JsonArray([.. ids.Select(id => (JsonNode)id!)]),
        }.ToJsonString());

    [Fact]
    public async Task Group_ConfirmsFromTheTokensGroupsClaimWithoutCallingGraph()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.WithGroups("grp-ops") };
        var http = new StubHttpClient();
        var provider = new GroupProvider(http, tokens);

        var access = await provider.EffectiveAccessAsync(
            Assignment(new GroupScope("grp-ops", GroupAccess.Member)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Confirmed);
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Group_IsNotYetWhenTheGroupsClaimIsMissingTheGroup()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.WithGroups("grp-other") };
        var provider = new GroupProvider(new StubHttpClient(), tokens);

        var access = await provider.EffectiveAccessAsync(
            Assignment(new GroupScope("grp-ops", GroupAccess.Member)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.NotYet);
    }

    [Fact]
    public async Task Group_FallsBackToGraphWhenTheTokenCarriesNoGroupClaims()
    {
        // The usual case: a registration that does not emit group claims.
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.With(c => c["oid"] = "caller") };
        var http = new StubHttpClient();
        http.On("POST", "checkMemberObjects", body: CheckMemberObjects("grp-ops"));
        var provider = new GroupProvider(http, tokens);

        var access = await provider.EffectiveAccessAsync(
            Assignment(new GroupScope("grp-ops", GroupAccess.Member)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Confirmed);
        http.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Group_IsNotYetWhenGraphDoesNotReportTheMembership()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.With(c => c["oid"] = "caller") };
        var http = new StubHttpClient();
        http.On("POST", "checkMemberObjects", body: CheckMemberObjects());
        var provider = new GroupProvider(http, tokens);

        var access = await provider.EffectiveAccessAsync(
            Assignment(new GroupScope("grp-ops", GroupAccess.Member)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.NotYet);
    }

    [Fact]
    public async Task Group_CannotTellForOwnership()
    {
        var provider = new GroupProvider(new StubHttpClient(), new FakeTokenProvider());

        var access = await provider.EffectiveAccessAsync(
            Assignment(new GroupScope("grp-ops", GroupAccess.Owner)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Unknown);
        access.Detail.Should().Contain("ownership");
    }

    [Fact]
    public async Task Group_CannotTellWhenGraphRefuses()
    {
        var tokens = new FakeTokenProvider { RefreshedToken = Jwt.With(c => c["oid"] = "caller") };
        var http = new StubHttpClient();
        http.On("POST", "checkMemberObjects", status: 403);
        var provider = new GroupProvider(http, tokens);

        var access = await provider.EffectiveAccessAsync(
            Assignment(new GroupScope("grp-ops", GroupAccess.Member)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Unknown);
    }

    // MARK: Azure resource roles

    private const string Reader = "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/rd1";

    private static byte[] Definition(params string[] actions) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["id"] = Reader,
            ["properties"] = new JsonObject
            {
                ["roleName"] = "Reader",
                ["permissions"] = new JsonArray(new JsonObject
                {
                    ["actions"] = new JsonArray([.. actions.Select(a => (JsonNode)a!)]),
                    ["notActions"] = new JsonArray(),
                }),
            },
        }.ToJsonString());

    private static byte[] Permissions(params string[] actions) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["value"] = new JsonArray(new JsonObject
            {
                ["actions"] = new JsonArray([.. actions.Select(a => (JsonNode)a!)]),
                ["notActions"] = new JsonArray(),
            }),
        }.ToJsonString());

    private static (AzureResourceProvider Provider, StubHttpClient Http) MakeAzure()
    {
        var http = new StubHttpClient();
        return (new AzureResourceProvider(http, new FakeTokenProvider()), http);
    }

    [Fact]
    public async Task Azure_ConfirmsWhenArmAlreadyGrantsWhatTheRoleGrants()
    {
        var (provider, http) = MakeAzure();
        http.On("GET", "roleDefinitions/rd1", body: Definition("Microsoft.Compute/virtualMachines/read"));
        http.On("GET", "Microsoft.Authorization/permissions", body: Permissions("Microsoft.Compute/*"));

        var access = await provider.EffectiveAccessAsync(
            Assignment(new AzureResourceScope("/subscriptions/sub1", Reader)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Confirmed);
    }

    [Fact]
    public async Task Azure_IsNotYetWhileArmStillGrantsLess()
    {
        var (provider, http) = MakeAzure();
        http.On("GET", "roleDefinitions/rd1", body: Definition("Microsoft.Compute/virtualMachines/write"));
        http.On("GET", "Microsoft.Authorization/permissions", body: Permissions("*/read"));

        var access = await provider.EffectiveAccessAsync(
            Assignment(new AzureResourceScope("/subscriptions/sub1", Reader)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.NotYet);
    }

    [Fact]
    public async Task Azure_ReadsTheProbeAtTheActivatedScope()
    {
        var (provider, http) = MakeAzure();
        http.On("GET", "roleDefinitions/rd1", body: Definition("*/read"));
        http.On("GET", "Microsoft.Authorization/permissions", body: Permissions("*"));

        await provider.EffectiveAccessAsync(
            Assignment(new AzureResourceScope("/subscriptions/sub1/resourceGroups/rg1", Reader)), TestIdentity);

        http.Requests.Should().Contain(r =>
            r.Url.AbsoluteUri.Contains("resourceGroups/rg1/providers/Microsoft.Authorization/permissions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Azure_ReadsARefusalAtTheScopeAsPropagationRatherThanAnError()
    {
        // Until the assignment reaches the scope, ARM will not even let the caller read its own permissions.
        var (provider, http) = MakeAzure();
        http.On("GET", "roleDefinitions/rd1", body: Definition("*/read"));
        http.On("GET", "Microsoft.Authorization/permissions", status: 403);

        var access = await provider.EffectiveAccessAsync(
            Assignment(new AzureResourceScope("/subscriptions/sub1", Reader)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.NotYet);
    }

    [Fact]
    public async Task Azure_CannotTellWhenTheRoleDefinitionSaysNothing()
    {
        var (provider, http) = MakeAzure();
        http.On("GET", "roleDefinitions/rd1", body: Definition());
        http.On("GET", "Microsoft.Authorization/permissions", body: Permissions("*"));

        var access = await provider.EffectiveAccessAsync(
            Assignment(new AzureResourceScope("/subscriptions/sub1", Reader)), TestIdentity);

        access.Kind.Should().Be(EffectiveAccessKind.Unknown);
    }

    // MARK: Claims

    [Fact]
    public void DirectoryRoles_AreNullForAnOpaqueToken() =>
        AccessTokenClaims.DirectoryRoles("not-a-jwt").Should().BeNull();

    [Fact]
    public void DirectoryRoles_AreNullWhenTheClaimIsAbsent() =>
        AccessTokenClaims.DirectoryRoles(Jwt.With(c => c["oid"] = "x")).Should().BeNull();

    [Fact]
    public void GroupMemberships_AreLowerCasedSoTheComparisonDoesNotDependOnCasing() =>
        AccessTokenClaims.GroupMemberships(Jwt.WithGroups("GRP-Ops")).Should().Equal("grp-ops");

    [Fact]
    public void ArrayClaims_IgnoreNonStringMembers()
    {
        var token = Jwt.With(c => c["wids"] = new JsonArray("a", 3, JsonValue.Create(true)));
        AccessTokenClaims.DirectoryRoles(token).Should().Equal("a");
    }

    [Fact]
    public void PropagationHints_TakeTheLongestDeadlineAcrossKinds() =>
        PropagationHints.Deadline([RoleScopeKind.EntraDirectory, RoleScopeKind.AzureResource])
            .Should().Be(PropagationHints.Deadline(RoleScopeKind.AzureResource));

    [Fact]
    public void PropagationHints_FallBackWhenThereAreNoKinds() =>
        PropagationHints.Deadline([]).Should().Be(PropagationHints.Deadline(RoleScopeKind.EntraDirectory));
}
