using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class TokenCacheHintTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    private static RoleKey Entra(string identity = "i") => new(identity, "t", new EntraDirectoryScope("r", "/"));

    private static RoleKey Azure(string identity = "i") => new(identity, "t", new AzureResourceScope("/subscriptions/s1", "owner"));

    private static RoleKey Group(string identity = "i") => new(identity, "t", new GroupScope("g1", GroupAccess.Member));

    private static ActivationOutcome Activated(RoleKey key) =>
        new(key, new ActivationResult.Activated(new ActiveAssignment(key, "a", Now, Now.AddHours(1), AssignmentStatus.Active)));

    private static ActivationOutcome Pending(RoleKey key) =>
        new(key, new ActivationResult.PendingApproval(new ActiveAssignment(key, "p", Now, null, AssignmentStatus.PendingApproval)));

    /// <summary>Every account signed in through the Azure CLI app, so the tool cache is shared.</summary>
    private static SignInMethod? Cli(string _) => SignInMethod.AzureCLI;

    [Fact]
    public void AzureAndGroupActivationsQualifyEntraDoesNot()
    {
        TokenCacheHint.Qualifies(Azure()).Should().BeTrue();
        TokenCacheHint.Qualifies(Group()).Should().BeTrue();
        TokenCacheHint.Qualifies(Entra()).Should().BeFalse();
    }

    [Fact]
    public void AffectedAccountsListsEachAccountOnceForActivatedAzureOrGroupRoles()
    {
        var outcomes = new[]
        {
            Activated(Entra("a")),
            Activated(Azure("a")),
            Activated(Group("a")),
            Activated(Group("b")),
            Pending(Azure("c")),
            new ActivationOutcome(Azure("d"), new ActivationResult.Failed(new PimException(PimErrorKind.Forbidden, "no"))),
        };

        TokenCacheHint.AffectedAccounts(outcomes, Cli).Should().Equal("a", "b");
    }

    [Fact]
    public void OnlyAccountsSharingTheToolTokenCacheAreAffected()
    {
        var methods = new Dictionary<string, SignInMethod>
        {
            ["cli"] = SignInMethod.AzureCLI,
            ["ps"] = SignInMethod.AzurePowerShell,
            ["app"] = SignInMethod.OwnApp,
            ["custom"] = SignInMethod.Custom("11111111-1111-1111-1111-111111111111"),
        };
        var outcomes = new[] { Activated(Azure("app")), Activated(Azure("cli")), Activated(Group("custom")), Activated(Group("ps")), Activated(Azure("gone")) };

        // An app registration has its own cache: Elevate cannot tell whether the tools were ever used as that account.
        TokenCacheHint.AffectedAccounts(outcomes, id => methods.GetValueOrDefault(id)).Should().Equal("cli", "ps");
        SignInMethod.AzureCLI.SharesToolTokenCache.Should().BeTrue();
        SignInMethod.AzurePowerShell.SharesToolTokenCache.Should().BeTrue();
        SignInMethod.OwnApp.SharesToolTokenCache.Should().BeFalse();
        methods["custom"].SharesToolTokenCache.Should().BeFalse();
    }

    [Fact]
    public void NothingQualifyingMeansNoAccounts()
    {
        TokenCacheHint.AffectedAccounts([Activated(Entra()), Pending(Group())], Cli).Should().BeEmpty();
        TokenCacheHint.AffectedAccounts([], Cli).Should().BeEmpty();
    }

    [Fact]
    public void WordingNamesTheAccountAndTheExactCommands()
    {
        TokenCacheHint.Message("alex@contoso.com").Should().Contain("alex@contoso.com").And.Contain("Azure CLI");
        TokenCacheHint.Advice.Should().Contain("az login").And.Contain("kubelogin remove-tokens");
        TokenCacheHint.Advice.Should().NotContain("--force-refresh", "az has no such flag; signing in again is the only way");
    }
}
