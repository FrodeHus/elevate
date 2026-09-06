using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ModelsTests
{
    private static RoleKey EntraKey(string identityId = "i", string tenantId = "t") =>
        new(identityId, tenantId, new EntraDirectoryScope("r", "/"));

    [Fact]
    public void RoleKey_DistinguishesTenants()
    {
        var scope = new EntraDirectoryScope("abc", "/");
        var a = new RoleKey("id1", "t1", scope);
        var b = new RoleKey("id1", "t2", scope);

        a.Should().NotBe(b);
        a.Should().Be(new RoleKey("id1", "t1", new EntraDirectoryScope("abc", "/")));
        a.TenantKey.Should().Be(new TenantKey("id1", "t1"));
    }

    [Fact]
    public void RoleScope_RoundTripsThroughJsonWithKindDiscriminator()
    {
        var scopes = new List<RoleScope>
        {
            new EntraDirectoryScope("r", "/"),
            new AzureResourceScope("/subscriptions/s", "d"),
            new GroupScope("g", GroupAccess.Owner),
        };

        var json = Json.Serialize(scopes);
        json.Should().Contain("\"kind\":\"entraDirectory\"")
            .And.Contain("\"kind\":\"azureResource\"")
            .And.Contain("\"kind\":\"group\"")
            .And.Contain("\"accessId\":\"owner\"");

        var back = Json.Deserialize<List<RoleScope>>(json);

        back.Should().Equal(scopes);
        scopes.Select(s => s.Kind).Should().Equal(
            RoleScopeKind.EntraDirectory, RoleScopeKind.AzureResource, RoleScopeKind.Group);
    }

    [Fact]
    public void ManualPolicy_HasTheExpectedDefaults()
    {
        var p = RolePolicy.ManualDefault;

        p.DefaultDuration.Should().Be(TimeSpan.FromSeconds(3600));
        p.MaximumDuration.Should().Be(TimeSpan.FromSeconds(8 * 3600));
        p.RequiresJustification.Should().BeTrue();
        p.RequiresTicket.Should().BeFalse();
        p.RequiresMfa.Should().BeFalse();
        p.RequiresApproval.Should().BeFalse();
        p.AuthenticationContext.Should().BeNull();
    }

    [Fact]
    public void Duration_EncodesAsSecondsAndAttosecondsArray()
    {
        var json = Json.Serialize(RolePolicy.ManualDefault);

        json.Should().Contain("\"defaultDuration\":[3600,0]")
            .And.Contain("\"maximumDuration\":[28800,0]")
            .And.Contain("\"requiresMFA\":false");

        Json.Deserialize<RolePolicy>(json).Should().Be(RolePolicy.ManualDefault);
    }

    [Fact]
    public void Duration_RoundTripsSubSecondComponents()
    {
        var request = new ActivationRequest(EntraKey(), TimeSpan.FromMilliseconds(1500), "because");

        var json = Json.Serialize(request);

        json.Should().Contain("\"duration\":[1,500000000000000000]");
        Json.Deserialize<ActivationRequest>(json).Should().Be(request);
    }

    [Fact]
    public void Duration_ReadsTheSwiftArrayForm()
    {
        var policy = Json.Deserialize<RolePolicy>(
            """
            {"defaultDuration":[1800,0],"maximumDuration":[3600,500000000000000000],
             "requiresJustification":true,"requiresTicket":false,"requiresMFA":true,"requiresApproval":false}
            """);

        policy!.DefaultDuration.Should().Be(TimeSpan.FromMinutes(30));
        policy.MaximumDuration.Should().Be(TimeSpan.FromSeconds(3600.5));
        policy.RequiresMfa.Should().BeTrue();
    }

    [Fact]
    public void Tenant_WithoutOptionalFieldsDecodesWithNoReasons()
    {
        var tenant = Json.Deserialize<TenantContext>(
            """{"identityId":"id1","tenantId":"t1","displayName":"Contoso","source":"home","discoveryMode":"automatic"}""");

        tenant.Should().NotBeNull();
        tenant!.AzureUnavailableReason.Should().BeNull();
        tenant.LastDiscoveryError.Should().BeNull();
        tenant.GroupsUnavailableReason.Should().BeNull();
        tenant.EntraActivation.Should().BeNull();
        tenant.Source.Should().Be(TenantSource.Home);
        tenant.DiscoveryMode.Should().Be(DiscoveryMode.Automatic);
        tenant.Key.Should().Be(new TenantKey("id1", "t1"));

        var off = tenant with
        {
            AzureUnavailableReason = "No Azure access in this tenant",
            EntraActivation = EntraActivationSupport.Unsupported("no scope"),
        };
        Json.Deserialize<TenantContext>(Json.Serialize(off)).Should().Be(off);
    }

    [Fact]
    public void EntraActivationSupport_UsesTheSwiftCaseObjectShape()
    {
        Json.Serialize(EntraActivationSupport.Supported).Should().Be("""{"supported":{}}""");
        Json.Serialize(EntraActivationSupport.Unsupported("nope")).Should().Be("""{"unsupported":{"reason":"nope"}}""");
        Json.Deserialize<EntraActivationSupport>("""{"supported":{}}""").Should().Be(EntraActivationSupport.Supported);
        Json.Deserialize<EntraActivationSupport>("""{"unsupported":{"reason":"nope"}}""")!.Reason.Should().Be("nope");
    }

    [Fact]
    public void ActiveAssignment_StatusRoundTrips()
    {
        var a = new ActiveAssignment(
            EntraKey(), "x", DateTimeOffset.FromUnixTimeSeconds(0), null, AssignmentStatus.Failed, "boom");

        var json = Json.Serialize(a);

        json.Should().Contain("\"status\":\"failed\"")
            .And.Contain("\"startDateTime\":\"1970-01-01T00:00:00Z\"")
            .And.NotContain("endDateTime");
        Json.Deserialize<ActiveAssignment>(json).Should().Be(a);

        var active = a with { Status = AssignmentStatus.PendingProvisioning, FailureReason = null, EndDateTime = DateTimeOffset.FromUnixTimeSeconds(3600) };
        Json.Serialize(active).Should().Contain("\"status\":\"pendingProvisioning\"");
        Json.Deserialize<ActiveAssignment>(Json.Serialize(active)).Should().Be(active);
    }

    [Fact]
    public void EligibleRole_DecodesWithoutTheOptionalFields()
    {
        var role = new EligibleRole(EntraKey(), "R", RoleSource.Discovered, RolePolicy.ManualDefault);
        role.Detail.Should().BeNull();
        role.ViaGroup.Should().BeNull();

        var node = JsonNode.Parse(Json.Serialize(role))!.AsObject();
        node.Should().NotContainKey("viaGroup");
        node.Should().NotContainKey("detail");
        Json.Deserialize<EligibleRole>(node.ToJsonString()).Should().Be(role);

        var tagged = role with { ViaGroup = "Ops Admins", Detail = "Contoso subscription" };
        var round = Json.Deserialize<EligibleRole>(Json.Serialize(tagged))!;
        round.ViaGroup.Should().Be("Ops Admins");
        round.Detail.Should().Be("Contoso subscription");
    }

    [Fact]
    public void PimException_UnexpectedWithStatusZeroShowsTheDetail()
    {
        new PimException(PimErrorKind.Unexpected, "Enter the client ID as a GUID").UserMessage
            .Should().Be("Enter the client ID as a GUID");
        new PimException(PimErrorKind.Unexpected).UserMessage.Should().Be("Unexpected error");
        new PimException(PimErrorKind.Unexpected, "", 503).UserMessage.Should().Be("Unexpected response (503)");
        new PimException(PimErrorKind.Unexpected, "gateway", 503).UserMessage
            .Should().Be("Unexpected response (503): gateway");
        new PimException(PimErrorKind.Unexpected, new string('x', 400), 500).UserMessage
            .Should().HaveLength("Unexpected response (500): ".Length + 300);
    }

    [Theory]
    [InlineData(PimErrorKind.ConsentRequired, null, "Admin consent required for this tenant")]
    [InlineData(PimErrorKind.Forbidden, "denied", "Not permitted: denied")]
    [InlineData(PimErrorKind.SignInDeclined, null, "Sign-in for this tenant was not completed; press Refresh to try again")]
    [InlineData(PimErrorKind.InteractionRequired, null, "Sign in again")]
    [InlineData(PimErrorKind.ClaimsChallenge, "{\"claims\":1}", "Multi-factor authentication required")]
    [InlineData(PimErrorKind.NotEligible, null, "Not eligible for this role")]
    [InlineData(PimErrorKind.PolicyViolation, "Justification required", "Justification required")]
    [InlineData(PimErrorKind.PendingApproval, null, "Awaiting approval")]
    [InlineData(PimErrorKind.Network, "timed out", "Network error: timed out")]
    public void PimException_UserMessageMatchesSwift(PimErrorKind kind, string? detail, string expected)
    {
        var error = new PimException(kind, detail);

        error.UserMessage.Should().Be(expected);
        error.Message.Should().Be(expected);
        error.Kind.Should().Be(kind);
        error.Detail.Should().Be(detail);
        error.Status.Should().Be(0);
    }

    [Fact]
    public void Json_RejectsAMalformedDuration()
    {
        var act = () => Json.Deserialize<RolePolicy>(
            """{"defaultDuration":3600,"maximumDuration":[1,0],"requiresJustification":true,"requiresTicket":false,"requiresMFA":false,"requiresApproval":false}""");

        act.Should().Throw<JsonException>();
    }
}
