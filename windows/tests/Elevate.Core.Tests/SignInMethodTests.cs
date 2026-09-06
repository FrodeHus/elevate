using System.Text.Json;
using Elevate.Core;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class SignInMethodTests
{
    [Fact]
    public void FirstPartyClientIds()
    {
        SignInMethod.OwnApp.ClientId.Should().BeNull();
        SignInMethod.AzureCLI.ClientId.Should().Be("04b07795-8ddb-461a-bbee-02f9e1bf7b46");
        SignInMethod.AzurePowerShell.ClientId.Should().Be("1950a258-227b-4e31-a9cf-717495945fc2");
        SignInMethod.OwnApp.UsesMsal.Should().BeTrue();
        SignInMethod.AzureCLI.UsesMsal.Should().BeFalse();
        SignInMethod.BuiltIn.Should().HaveCount(3);

        var custom = SignInMethod.Custom("abc");
        custom.ClientId.Should().Be("abc");
        custom.IsCustom.Should().BeTrue();
        custom.UsesMsal.Should().BeFalse();
    }

    [Fact]
    public void DefaultValueIsOwnApp()
    {
        default(SignInMethod).Should().Be(SignInMethod.OwnApp);
    }

    [Fact]
    public void CustomMethodRoundTripsAndKeepsLegacyKeys()
    {
        var custom = SignInMethod.Custom("11111111-2222-3333-4444-555555555555");

        var encoded = Json.Serialize(custom);
        encoded.Should().Be("\"custom:11111111-2222-3333-4444-555555555555\"");
        Json.Deserialize<SignInMethod>(encoded).Should().Be(custom);
        Json.Deserialize<SignInMethod>("\"azureCLI\"").Should().Be(SignInMethod.AzureCLI);
        Json.Deserialize<SignInMethod>("\"ownApp\"").Should().Be(SignInMethod.OwnApp);

        SignInMethod.TryFromStorageKey("custom:", out _).Should().BeFalse();
        var act = () => Json.Deserialize<SignInMethod>("\"bogus\"");
        act.Should().Throw<JsonException>();

        custom.IsPreauthorisedForEntraActivation.Should().BeTrue();
        custom.LimitationSummary.Should().BeNull();
        SignInMethod.AzureCLI.IsPreauthorisedForEntraActivation.Should().BeFalse();
        SignInMethod.AzureCLI.LimitationSummary.Should()
            .Be("Supports Azure resource roles only. Entra roles are neither read nor activated.");
        SignInMethod.AzureCLI.EntraViewOnlyReason.Should().StartWith("This account was added with the Azure CLI app");
        SignInMethod.OwnApp.EntraViewOnlyReason.Should().BeNull();
    }

    [Fact]
    public void IdentityDefaultsToOwnAppWhenFieldMissing()
    {
        var identity = Json.Deserialize<Identity>(
            """{"id":"oid.tid","upn":"u@x","displayName":"U","homeTenantId":"tid"}""");

        identity.Should().NotBeNull();
        identity!.SignInMethod.Should().Be(SignInMethod.OwnApp);

        var round = Json.Deserialize<Identity>(
            Json.Serialize(new Identity("a.b", "u", "U", "b", SignInMethod.AzureCLI)));
        round!.SignInMethod.Should().Be(SignInMethod.AzureCLI);
        Json.Serialize(round).Should().Contain("\"signInMethod\":\"azureCLI\"");
    }

    [Fact]
    public void IdentityDecodesAnExplicitNullSignInMethodAsOwnApp()
    {
        var identity = Json.Deserialize<Identity>(
            """{"id":"oid.tid","upn":"u@x","displayName":"U","homeTenantId":"tid","signInMethod":null}""");

        identity!.SignInMethod.Should().Be(SignInMethod.OwnApp);
    }
}
