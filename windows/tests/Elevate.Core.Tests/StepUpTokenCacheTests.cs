using System.Text.Json;
using Elevate.Core.Auth;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>StepUpTokenCacheTests</c>.</summary>
public class StepUpTokenCacheTests
{
    private static readonly string[] Scopes =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/RoleManagementPolicy.Read.Directory",
    ];

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void HandsBackTheStepUpTokenForTheSameIdentityTenantAndScopes()
    {
        var cache = new StepUpTokenCache();
        var stepped = Token(DateTimeOffset.UtcNow.AddHours(1));

        cache.Store(stepped, "a", "t", Scopes);

        cache.Token("a", "t", [.. Scopes.Reverse()]).Should().Be(stepped);
    }

    [Fact]
    public void HoldsNothingForAnotherIdentityTenantOrScopeSet()
    {
        var cache = new StepUpTokenCache();
        cache.Store(Token(DateTimeOffset.UtcNow.AddHours(1)), "a", "t", Scopes);

        cache.Token("b", "t", Scopes).Should().BeNull();
        cache.Token("a", "other", Scopes).Should().BeNull();
        cache.Token("a", "t", ["https://management.azure.com/user_impersonation"]).Should().BeNull();
    }

    [Fact]
    public void DropsTheTokenOnceItsOwnExpiryIsNear()
    {
        var now = Start;
        var cache = new StepUpTokenCache(() => now);
        cache.Store(Token(Start.AddMinutes(10)), "a", "t", Scopes);

        now = Start.AddMinutes(10) - StepUpTokenCache.Skew;

        cache.Token("a", "t", Scopes).Should().BeNull();
    }

    [Fact]
    public void AnOpaqueTokenIsHeldOnlyLongEnoughForTheRetry()
    {
        var now = Start;
        var cache = new StepUpTokenCache(() => now);
        cache.Store("not-a-jwt", "a", "t", Scopes);

        cache.Token("a", "t", Scopes).Should().Be("not-a-jwt");

        now = Start + StepUpTokenCache.OpaqueLifetime;

        cache.Token("a", "t", Scopes).Should().BeNull();
    }

    [Fact]
    public void SigningOutForgetsWhatWasHeldForThatIdentityAlone()
    {
        var cache = new StepUpTokenCache();
        var kept = Token(DateTimeOffset.UtcNow.AddHours(1));
        cache.Store(Token(DateTimeOffset.UtcNow.AddHours(1)), "a", "t", Scopes);
        cache.Store(kept, "b", "t", Scopes);

        cache.Forget("a");

        cache.Token("a", "t", Scopes).Should().BeNull();
        cache.Token("b", "t", Scopes).Should().Be(kept);
    }

    /// <summary>A JWT-shaped token that expires at the given moment.</summary>
    private static string Token(DateTimeOffset expiry)
    {
        var payload = new Dictionary<string, object>
        {
            ["aud"] = "https://graph.microsoft.com",
            ["acrs"] = "c10",
            ["exp"] = expiry.ToUnixTimeSeconds(),
        };
        var b64 = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"eyJhbGciOiJub25lIn0.{b64}.sig";
    }
}
