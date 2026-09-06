using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core;
using Elevate.Core.Models;
using Elevate.Core.Support;
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
    public void RoleScope_UsesSwiftsSingleKeyCaseObjects()
    {
        const string entra = """{"entraDirectory":{"roleDefinitionId":"r","directoryScopeId":"/"}}""";
        const string azure = """{"azureResource":{"scope":"/subscriptions/s","roleDefinitionId":"d"}}""";
        const string group = """{"group":{"groupId":"g","accessId":"member"}}""";

        Json.Serialize<RoleScope>(new EntraDirectoryScope("r", "/")).Should().Be(entra);
        Json.Serialize<RoleScope>(new AzureResourceScope("/subscriptions/s", "d")).Should().Be(azure);
        Json.Serialize<RoleScope>(new GroupScope("g", GroupAccess.Member)).Should().Be(group);

        Json.Deserialize<RoleScope>(entra).Should().Be(new EntraDirectoryScope("r", "/"));
        Json.Deserialize<RoleScope>(azure).Should().Be(new AzureResourceScope("/subscriptions/s", "d"));
        Json.Deserialize<RoleScope>(group).Should().Be(new GroupScope("g", GroupAccess.Member));

        Json.Serialize<RoleScope>(new GroupScope("g", GroupAccess.Owner))
            .Should().Be("""{"group":{"groupId":"g","accessId":"owner"}}""");
    }

    [Fact]
    public void RoleScope_RoundTripsThroughJson()
    {
        var scopes = new List<RoleScope>
        {
            new EntraDirectoryScope("r", "/"),
            new AzureResourceScope("/subscriptions/s", "d"),
            new GroupScope("g", GroupAccess.Owner),
        };

        var json = Json.Serialize(scopes);
        json.Should().NotContain("\"kind\"");

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
    public void Duration_EncodesAsSwifts128BitAttosecondPair()
    {
        var json = Json.Serialize(RolePolicy.ManualDefault);

        // Swift's stdlib Duration encodes the raw 128-bit attosecond value as [_high, _low].
        json.Should().Contain("\"defaultDuration\":[195,2884905626637434880]")
            .And.Contain("\"maximumDuration\":[1561,4632500939389927424]")
            .And.Contain("\"requiresMFA\":false");

        Json.Deserialize<RolePolicy>(json).Should().Be(RolePolicy.ManualDefault);
    }

    [Theory]
    [InlineData(3600, 195, 2884905626637434880UL)]
    [InlineData(28800, 1561, 4632500939389927424UL)]
    [InlineData(1800, 97, 10665824850173493248UL)]
    [InlineData(1, 0, 1000000000000000000UL)]
    public void Duration_SplitsTheAttosecondValueIntoHighAndLowWords(int seconds, long high, ulong low)
    {
        var holder = new DurationHolder(TimeSpan.FromSeconds(seconds));

        var json = Json.Serialize(holder);

        json.Should().Be($$"""{"lastDuration":[{{high}},{{low}}]}""");
        Json.Deserialize<DurationHolder>(json).Should().Be(holder);
    }

    [Fact]
    public void Duration_ReadsAVerbatimMacOsStateSnippet()
    {
        // Copied from a real macOS state.json; the low word of 30 minutes exceeds Int64.MaxValue.
        Json.Deserialize<DurationHolder>("""{"lastDuration":[1561,4632500939389927424]}""")!
            .LastDuration.Should().Be(TimeSpan.FromHours(8));

        Json.Deserialize<DurationHolder>("""{"lastDuration":[97,10665824850173493248]}""")!
            .LastDuration.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Duration_RoundTripsNegativeValues()
    {
        var holder = new DurationHolder(TimeSpan.FromHours(-8));

        var json = Json.Serialize(holder);

        json.Should().Be("""{"lastDuration":[-1562,13814243134319624192]}""");
        Json.Deserialize<DurationHolder>(json).Should().Be(holder);
    }

    [Fact]
    public void Duration_RoundTripsSubSecondComponents()
    {
        var request = new ActivationRequest(EntraKey(), TimeSpan.FromMilliseconds(1500), "because");

        var json = Json.Serialize(request);

        json.Should().Contain("\"duration\":[0,1500000000000000000]");
        Json.Deserialize<ActivationRequest>(json).Should().Be(request);
    }

    [Fact]
    public void Duration_ReadsTheSwiftArrayForm()
    {
        var policy = Json.Deserialize<RolePolicy>(
            """
            {"defaultDuration":[97,10665824850173493248],"maximumDuration":[195,3384905626637434880],
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
    public void AssignmentStatus_UsesSwiftsSingleKeyCaseObjects()
    {
        Json.Serialize(AssignmentStatus.Failed("x")).Should().Be("""{"failed":{"_0":"x"}}""");
        Json.Serialize(AssignmentStatus.Active).Should().Be("""{"active":{}}""");
        Json.Serialize(AssignmentStatus.PendingApproval).Should().Be("""{"pendingApproval":{}}""");
        Json.Serialize(AssignmentStatus.PendingProvisioning).Should().Be("""{"pendingProvisioning":{}}""");
        Json.Serialize(AssignmentStatus.Scheduled).Should().Be("""{"scheduled":{}}""");

        Json.Deserialize<AssignmentStatus>("""{"failed":{"_0":"x"}}""").Should().Be(AssignmentStatus.Failed("x"));
        Json.Deserialize<AssignmentStatus>("""{"active":{}}""").Should().Be(AssignmentStatus.Active);
        Json.Deserialize<AssignmentStatus>("""{"pendingApproval":{}}""").Should().Be(AssignmentStatus.PendingApproval);
        Json.Deserialize<AssignmentStatus>("""{"pendingProvisioning":{}}""").Should().Be(AssignmentStatus.PendingProvisioning);
        Json.Deserialize<AssignmentStatus>("""{"scheduled":{}}""").Should().Be(AssignmentStatus.Scheduled);

        var bad = () => Json.Deserialize<AssignmentStatus>("""{"failed":"x"}""");
        bad.Should().Throw<JsonException>();
    }

    [Fact]
    public void ActiveAssignment_StatusRoundTrips()
    {
        var a = new ActiveAssignment(
            EntraKey(), "x", DateTimeOffset.FromUnixTimeSeconds(0), null, AssignmentStatus.Failed("boom"));

        var json = Json.Serialize(a);

        json.Should().Contain("""
            "status":{"failed":{"_0":"boom"}}
            """.Trim())
            .And.Contain("\"startDateTime\":\"1970-01-01T00:00:00Z\"")
            .And.NotContain("endDateTime");
        Json.Deserialize<ActiveAssignment>(json).Should().Be(a);

        var active = a with { Status = AssignmentStatus.PendingProvisioning, EndDateTime = DateTimeOffset.FromUnixTimeSeconds(3600) };
        Json.Serialize(active).Should().Contain("""
            "status":{"pendingProvisioning":{}}
            """.Trim());
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

    [Fact]
    public void ActiveAssignment_MatchesTheMacOsLiteralIncludingItsNestedScope()
    {
        const string Literal =
            """{"roleKey":{"identityId":"i","tenantId":"t","scope":{"azureResource":{"scope":"/subscriptions/s1/resourceGroups/rg","roleDefinitionId":"rd1"}}},"assignmentId":"a1","startDateTime":"1970-01-01T00:00:00Z","endDateTime":"1970-01-01T01:00:00Z","status":{"active":{}}}""";

        var assignment = new ActiveAssignment(
            new RoleKey("i", "t", new AzureResourceScope("/subscriptions/s1/resourceGroups/rg", "rd1")),
            "a1",
            DateTimeOffset.FromUnixTimeSeconds(0),
            DateTimeOffset.FromUnixTimeSeconds(3600),
            AssignmentStatus.Active);

        Json.Serialize(assignment).Should().Be(Literal);

        var back = Json.Deserialize<ActiveAssignment>(Literal);
        back.Should().Be(assignment);
        back!.RoleKey.Scope.Should().BeOfType<AzureResourceScope>()
            .Which.Scope.Should().Be("/subscriptions/s1/resourceGroups/rg");
    }

    [Fact]
    public void Json_RejectsNonStringAndNonObjectPayloads()
    {
        var date = () => Json.Deserialize<ActiveAssignment>(
            """{"roleKey":{"identityId":"i","tenantId":"t","scope":{"group":{"groupId":"g","accessId":"member"}}},"startDateTime":123,"status":{"active":{}}}""");
        date.Should().Throw<JsonException>();

        var signIn = () => Json.Deserialize<Identity>("""{"id":"i","upn":"u","displayName":"d","homeTenantId":"t","signInMethod":7}""");
        signIn.Should().Throw<JsonException>();

        var support = () => Json.Deserialize<EntraActivationSupport>("""{"supported":"yes"}""");
        support.Should().Throw<JsonException>();

        var status = () => Json.Deserialize<AssignmentStatus>("""{"active":{"_0":"x"}}""");
        status.Should().Throw<JsonException>();
    }

    private sealed record DurationHolder(TimeSpan LastDuration);
}

public class TextTests
{
    [Fact]
    public void PrefixCountsUserPerceivedCharacters()
    {
        Text.Prefix("abc", 5).Should().Be("abc");
        Text.Prefix("abcdef", 3).Should().Be("abc");
        // Two flag emoji are four UTF-16 code units each; a code-unit cut would split them.
        Text.Prefix("🇳🇴🇩🇰x", 2).Should().Be("🇳🇴🇩🇰");
        Text.Prefix("éx", 1).Should().Be("é");
    }

    [Fact]
    public void UnexpectedResponseTruncatesByCharacterNotCodeUnit()
    {
        var body = string.Concat(Enumerable.Repeat("🇳🇴", 400));
        var message = new PimException(PimErrorKind.Unexpected, body, 500).UserMessage;
        message.Should().StartWith("Unexpected response (500): ");
        message.Should().EndWith("🇳🇴");
        new System.Globalization.StringInfo(message["Unexpected response (500): ".Length..]).LengthInTextElements.Should().Be(300);
    }

    [Fact]
    public void JsonKeepsNonAsciiTextUnescaped()
    {
        Json.Serialize(new TicketInfo("42", "Jira Ø")).Should().Contain("Jira Ø");
    }
}
