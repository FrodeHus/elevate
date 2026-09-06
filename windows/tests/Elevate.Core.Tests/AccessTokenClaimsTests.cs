using System.Text.Json;
using Elevate.Core.Auth;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AccessTokenClaimsTests</c>.</summary>
public class AccessTokenClaimsTests
{
    /// <summary>A JWT-shaped token whose <c>scp</c> claim carries the given space-separated scopes.</summary>
    private static string Token(string? scp)
    {
        var payload = new Dictionary<string, string> { ["aud"] = "https://graph.microsoft.com", ["tid"] = "t" };
        if (scp is not null)
        {
            payload["scp"] = scp;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var b64 = Convert.ToBase64String(body).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"eyJhbGciOiJub25lIn0.{b64}.sig";
    }

    [Fact]
    public void ReadsScopesFromScpClaim()
    {
        var scopes = AccessTokenClaims.GrantedScopes(Token("User.Read RoleEligibilitySchedule.Read.Directory"));
        scopes.Should().BeEquivalentTo(["User.Read", "RoleEligibilitySchedule.Read.Directory"]);
    }

    [Fact]
    public void ReadOnlyTokenDoesNotPermitActivation()
    {
        AccessTokenClaims.PermitsEntraActivation(
                Token("User.Read RoleEligibilitySchedule.Read.Directory RoleAssignmentSchedule.Read.Directory"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("RoleAssignmentSchedule.ReadWrite.Directory")]
    [InlineData("RoleManagement.ReadWrite.Directory")]
    [InlineData("PrivilegedAccess.ReadWrite.AzureAD")]
    public void AnyWriteScopePermitsActivation(string scope)
    {
        AccessTokenClaims.PermitsEntraActivation(Token($"User.Read {scope}")).Should().BeTrue();
    }

    [Fact]
    public void OpaqueOrScopelessTokenIsUnknown()
    {
        AccessTokenClaims.PermitsEntraActivation("not-a-jwt").Should().BeNull();
        AccessTokenClaims.PermitsEntraActivation(Token(null)).Should().BeNull();
    }
}
