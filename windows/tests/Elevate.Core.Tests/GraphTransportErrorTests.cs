using System.Text;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>GraphTransportErrorTests</c>.</summary>
public class GraphTransportErrorTests
{
    private static HttpResponseData Response(int status, string body = "", IReadOnlyDictionary<string, string>? headers = null)
        => new(status, headers ?? new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body));

    [Fact]
    public void EarlyDeactivation_MapsToClearPolicyMessage()
    {
        var response = Response(400,
            """{"error":{"code":"ActiveDurationTooShort","message":"The role assignment cannot be deactivated within 5 minutes of activation."}}""");

        var error = GraphTransport.MapError(response);

        error.Kind.Should().Be(PimErrorKind.PolicyViolation);
        error.Detail.Should().Be("PIM requires a role to stay active for 5 minutes before it can be deactivated");
    }

    [Fact]
    public void Throttling_MapsToNetworkErrorWithRetryAfter()
    {
        var withHeader = GraphTransport.MapError(Response(429, headers: new Dictionary<string, string> { ["Retry-After"] = "12" }));
        withHeader.Kind.Should().Be(PimErrorKind.Network);
        withHeader.Detail.Should().Be("Throttled by the service; retry in 12s");

        var withoutHeader = GraphTransport.MapError(Response(429));
        withoutHeader.Kind.Should().Be(PimErrorKind.Network);
        withoutHeader.Detail.Should().Be("Throttled by the service; retry in a few seconds");
    }

    [Fact]
    public void ArmForbidden_IsAPermissionFailureNotConsent()
    {
        var error = GraphTransport.MapArmError(Response(403, """{"error":{"code":"AuthorizationFailed","message":"no"}}"""));

        error.Kind.Should().Be(PimErrorKind.PolicyViolation);
        error.Detail.Should().Be("Not permitted at this scope");
    }

    [Fact]
    public void Arm_SharesClaimsAndThrottlingMapping()
    {
        const string claims = """{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}""";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims));
        var challenge = GraphTransport.MapArmError(Response(401, headers: new Dictionary<string, string>
        {
            ["WWW-Authenticate"] = $"""Bearer error="insufficient_claims", claims="{b64}" """,
        }));
        challenge.Kind.Should().Be(PimErrorKind.ClaimsChallenge);
        challenge.Detail.Should().Be(claims);

        var throttled = GraphTransport.MapArmError(Response(429, headers: new Dictionary<string, string> { ["Retry-After"] = "3" }));
        throttled.Kind.Should().Be(PimErrorKind.Network);
        throttled.Detail.Should().Be("Throttled by the service; retry in 3s");

        var early = GraphTransport.MapArmError(Response(400, """{"error":{"code":"ActiveDurationTooShort","message":"x"}}"""));
        early.Kind.Should().Be(PimErrorKind.PolicyViolation);
        early.Detail.Should().Be("PIM requires a role to stay active for 5 minutes before it can be deactivated");
    }

    [Fact]
    public async Task Transport_UsesInjectedMapperAndSupportsPut()
    {
        var http = new StubHttpClient();
        http.On("PUT", "example.test", "{}", 403);
        var transport = new GraphTransport(http, new FakeTokenProvider(), GraphTransport.MapArmError);
        var identity = new Identity("i", "u", "U", "t");

        var act = () => transport.PutAsync(
            identity, "t", new Uri("https://example.test/x"), Scopes.ArmAll, Encoding.UTF8.GetBytes("{}"));

        var thrown = (await act.Should().ThrowAsync<PimException>()).Which;
        thrown.Kind.Should().Be(PimErrorKind.PolicyViolation);
        thrown.Detail.Should().Be("Not permitted at this scope");

        var request = http.Requests[0];
        request.Method.Should().Be("PUT");
        request.Headers["Content-Type"].Should().Be("application/json");
        request.Headers["Authorization"].Should().Be("Bearer token-t");
    }
}
