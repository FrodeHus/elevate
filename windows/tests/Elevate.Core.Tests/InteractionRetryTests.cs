using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class InteractionRetryTests
{
    private static readonly Identity TestIdentity = new("i", "u@x", "U", "t");

    private static Task<T> Run<T>(FakeTokenProvider tokens, Func<Task<T>> operation, string? fallbackClaims = null)
        => InteractionRetry.RunAsync(tokens, TestIdentity, "t", Scopes.GraphAll, operation, fallbackClaims);

    [Fact]
    public async Task RunAsync_ReturnsFirstTrySuccessWithoutInteraction()
    {
        var tokens = new FakeTokenProvider();
        var calls = 0;

        var result = await Run(tokens, () => { calls++; return Task.FromResult("ok"); });

        result.Should().Be("ok");
        calls.Should().Be(1);
        tokens.InteractiveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_OnInteractionRequired_AcquiresInteractivelyThenRetries()
    {
        var tokens = new FakeTokenProvider();
        var calls = 0;

        var result = await Run(tokens, () =>
        {
            calls++;
            return calls == 1
                ? throw new PimException(PimErrorKind.InteractionRequired)
                : Task.FromResult("ok");
        });

        result.Should().Be("ok");
        calls.Should().Be(2);
        tokens.InteractiveCalls.Should().ContainSingle();
        tokens.InteractiveCalls[0].Claims.Should().BeNull();
        tokens.InteractiveCalls[0].TenantId.Should().Be("t");
        tokens.InteractiveCalls[0].Scopes.Should().BeEquivalentTo(Scopes.GraphAll);
    }

    [Fact]
    public async Task RunAsync_OnInteractionRequired_SendsTheFallbackClaims()
    {
        var tokens = new FakeTokenProvider();
        var calls = 0;

        var result = await Run(
            tokens,
            () =>
            {
                calls++;
                return calls == 1
                    ? throw new PimException(PimErrorKind.InteractionRequired)
                    : Task.FromResult("ok");
            },
            fallbackClaims: Elevate.Core.Networking.ClaimsChallenge.MultiFactor);

        result.Should().Be("ok");
        tokens.InteractiveCalls.Should().ContainSingle()
            .Which.Claims.Should().Be(Elevate.Core.Networking.ClaimsChallenge.MultiFactor);
    }

    [Fact]
    public async Task RunAsync_OnClaimsChallenge_PassesTheChallengeClaimsThrough()
    {
        var tokens = new FakeTokenProvider();
        var calls = 0;

        var result = await Run(tokens, () =>
        {
            calls++;
            return calls == 1
                ? throw new PimException(PimErrorKind.ClaimsChallenge, "{}")
                : Task.FromResult("ok");
        });

        result.Should().Be("ok");
        calls.Should().Be(2);
        tokens.InteractiveCalls.Should().ContainSingle().Which.Claims.Should().Be("{}");
    }

    [Fact]
    public async Task RunAsync_PropagatesASecondFailure()
    {
        var tokens = new FakeTokenProvider();
        var calls = 0;

        var act = async () => await Run<string>(tokens, () =>
        {
            calls++;
            throw new PimException(PimErrorKind.ClaimsChallenge, "{}");
        });

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.ClaimsChallenge);
        calls.Should().Be(2);
        tokens.InteractiveCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_PropagatesAFailedInteractiveAcquisition()
    {
        var tokens = new FakeTokenProvider { InteractiveError = new PimException(PimErrorKind.SignInDeclined) };
        var calls = 0;

        var act = async () => await Run<string>(tokens, () =>
        {
            calls++;
            throw new PimException(PimErrorKind.InteractionRequired);
        });

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.SignInDeclined);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_DoesNotInteractForOtherErrors()
    {
        var tokens = new FakeTokenProvider();
        var calls = 0;

        var act = async () => await Run<string>(tokens, () =>
        {
            calls++;
            throw new PimException(PimErrorKind.Forbidden, "nope");
        });

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.Forbidden);
        calls.Should().Be(1);
        tokens.InteractiveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_DoesNotInteractForNonPimExceptions()
    {
        var tokens = new FakeTokenProvider();

        var act = async () => await Run<string>(tokens, () => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        tokens.InteractiveCalls.Should().BeEmpty();
    }
}
