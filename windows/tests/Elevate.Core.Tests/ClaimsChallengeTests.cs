using System.Text;
using Elevate.Core.Networking;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ClaimsChallengeTests
{
    [Fact]
    public void Parse_ExtractsAndDecodesClaims()
    {
        const string json = """{"access_token":{"acrs":{"essential":true,"values":["c1"]}}}""";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=');
        var header =
            $"""Bearer realm="", authorization_uri="https://login.microsoftonline.com/common/oauth2/authorize", error="insufficient_claims", claims="{b64}" """;

        ClaimsChallenge.Parse(header).Should().Be(json);
    }

    [Fact]
    public void Parse_ReturnsNullWithoutClaims()
    {
        ClaimsChallenge.Parse("""Bearer realm="" """).Should().BeNull();
    }

    [Fact]
    public void Parse_ReturnsNullForUndecodableClaims()
    {
        ClaimsChallenge.Parse("""Bearer claims="!!!not base64!!!" """).Should().BeNull();
    }

    [Fact]
    public void MultiFactor_RequestsAnMfaAmrValue()
    {
        ClaimsChallenge.MultiFactor.Should().Be("""{"access_token":{"amr":{"values":["mfa"]}}}""");
    }

    [Fact]
    public void AuthenticationContext_EmbedsTheEscapedContextId()
    {
        ClaimsChallenge.AuthenticationContext("c1")
            .Should().Be("""{"access_token":{"acrs":{"essential":true,"value":"c1"}}}""");

        ClaimsChallenge.AuthenticationContext("""a"\b""")
            .Should().Be("""{"access_token":{"acrs":{"essential":true,"value":"a\"\\b"}}}""");
    }
}
