using Elevate.App.Services;
using Elevate.App.Tests.Support;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.App.Tests;

public class AppModelTokenHintTests
{
    private static ActivationOutcome Activated(RoleKey key)
    {
        var now = DateTimeOffset.UtcNow;
        return new ActivationOutcome(key, new ActivationResult.Activated(new ActiveAssignment(key, "a", now, now.AddHours(1), AssignmentStatus.Active)));
    }

    [Fact]
    public async Task AnAzureOrGroupActivationRaisesTheHintOnceUntilDismissedForTheAccount()
    {
        var state = new AppState { Identities = [Sample.Identity()], Tenants = [Sample.Tenant()] };
        using var test = await TestModel.BootstrappedAsync(state);
        var model = test.Model;

        model.NoteTokenHint([Activated(Sample.EntraKey)]);
        model.TokenHint.Should().BeNull("an Entra role does not touch the Azure CLI's token");

        model.NoteTokenHint([Activated(Sample.GroupKey), Activated(Sample.AzureKey)]);
        model.TokenHint.Should().NotBeNull();
        model.TokenHint!.Value.Account.Should().Be(Sample.Identity().Upn);

        model.DismissTokenHint();
        model.TokenHint.Should().BeNull();
        test.Settings.DismissedTokenHintAccounts.Should().BeEquivalentTo([Sample.Identity().Id]);
        new AppSettings(test.Directory).DismissedTokenHintAccounts.Should().BeEquivalentTo([Sample.Identity().Id], "the choice is persisted");

        model.NoteTokenHint([Activated(Sample.AzureKey)]);
        model.TokenHint.Should().BeNull("the account dismissed it");
    }
}
