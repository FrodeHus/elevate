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

        TokenCacheHint.AffectedAccounts(outcomes).Should().Equal("a", "b");
    }

    [Fact]
    public void NothingQualifyingMeansNoAccounts()
    {
        TokenCacheHint.AffectedAccounts([Activated(Entra()), Pending(Group())]).Should().BeEmpty();
        TokenCacheHint.AffectedAccounts([]).Should().BeEmpty();
    }

    [Fact]
    public void WordingNamesTheAccountAndTheExactCommands()
    {
        TokenCacheHint.Message("alex@contoso.com").Should().Contain("alex@contoso.com").And.Contain("Azure CLI");
        TokenCacheHint.Advice.Should().Contain("az account get-access-token --force-refresh").And.Contain("kubelogin remove-tokens");
        TokenCacheHint.Advice.Should().NotContain("az account clear", "that signs every account out");
    }
}
