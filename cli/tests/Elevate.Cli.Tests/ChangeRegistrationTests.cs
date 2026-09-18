using Elevate.Cli.Infrastructure;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// Moving an account between Entra app registrations: the account, its tenants, configured roles
/// and profiles survive, the change is saved only after the same user signs in, and the flags a
/// limited method left behind are cleared.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class ChangeRegistrationTests
{
    private const string SettingsClientId = "11111111-2222-3333-4444-555555555555";
    private const string OwnClientId = "aaaaaaaa-2222-3333-4444-555555555555";

    private static async Task<(int Code, string Out, string Err)> RunAsync(TestSession session, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await Program.Main([.. args, "--no-color", "--data-dir", session.Directory]);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private static void Replace(TestSession test, SignInMethod method)
    {
        var index = test.Session.State.Identities.FindIndex(i => i.Id == TestSession.Account.Id);
        test.Session.State.Identities[index] = TestSession.Account with { SignInMethod = method };
        test.Session.Persist();
    }

    [Fact]
    public async Task AnAzureCliAccountIsUpgradedToARegistrationOfItsOwn()
    {
        using var test = new TestSession();
        test.Settings.ClientId = SettingsClientId;
        Replace(test, SignInMethod.AzureCLI);
        // A flag the limited method left behind: Entra roles were view-only for it.
        test.Session.State.UpsertTenant(TestSession.Tenant with
        {
            EntraActivation = EntraActivationSupport.Unsupported("Azure resource roles only"),
        });
        test.Tokens.NextSignIn = TestSession.Account;

        var moved = await test.Session.ChangeRegistrationAsync(
            test.Session.Identities[0], SignInMethod.PinnedApp(OwnClientId));

        moved.SignInMethod.Should().Be(SignInMethod.PinnedApp(OwnClientId));
        test.Session.Identities.Should().ContainSingle().Which.SignInMethod.PinnedClientId.Should().Be(OwnClientId);
        test.Session.Tenants.Should().ContainSingle().Which.EntraActivation.Should().BeNull("the Azure CLI flag no longer applies");
        // Saved, not just held in memory.
        new Elevate.Core.Storage.AppStateStore(test.Directory).Load()
            .Identities.Should().ContainSingle().Which.SignInMethod.Should().Be(SignInMethod.PinnedApp(OwnClientId));
        // The old client's session is dropped; the new one is keyed by a different client id.
        test.Tokens.SignedOut.Should().ContainSingle().Which.SignInMethod.Should().Be(SignInMethod.AzureCLI);
    }

    [Fact]
    public async Task ADifferentAccountSigningInChangesNothing()
    {
        using var test = new TestSession();
        test.Settings.ClientId = SettingsClientId;
        Replace(test, SignInMethod.AzureCLI);
        test.Tokens.NextSignIn = new Identity("other", "sam@contoso.com", "Sam", "t1");

        var act = () => test.Session.ChangeRegistrationAsync(test.Session.Identities[0], SignInMethod.PinnedApp(OwnClientId));

        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Nothing was changed");
        test.Session.Identities.Should().ContainSingle().Which.SignInMethod.Should().Be(SignInMethod.AzureCLI);
        // The stray session belongs to nobody in the list, so it is discarded.
        test.Tokens.SignedOut.Should().ContainSingle().Which.Id.Should().Be("other");
    }

    [Fact]
    public async Task MovingOntoTheSettingsRegistrationKeepsTheSharedCacheSlot()
    {
        using var test = new TestSession();
        test.Settings.ClientId = SettingsClientId;
        Replace(test, SignInMethod.PinnedApp(SettingsClientId));
        test.Tokens.NextSignIn = TestSession.Account;

        var moved = await test.Session.ChangeRegistrationAsync(test.Session.Identities[0], SignInMethod.OwnApp);

        moved.SignInMethod.Should().Be(SignInMethod.OwnApp);
        // Both forms are the same client id, so the one MSAL entry must not be signed out.
        test.Tokens.SignedOut.Should().BeEmpty();
    }

    [Fact]
    public async Task AManagedClientIdAllowsOnlyTheManagedRegistration()
    {
        var managed = ManagedConfiguration.Load(
            new DictionaryManagedSource(new Dictionary<string, object?> { ["ClientId"] = SettingsClientId }, "unit"));
        using var test = new TestSession(managed);
        Replace(test, SignInMethod.AzureCLI);
        test.Tokens.NextSignIn = TestSession.Account;

        var act = () => test.Session.ChangeRegistrationAsync(test.Session.Identities[0], SignInMethod.PinnedApp(OwnClientId));
        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("managed by your organization");

        var moved = await test.Session.ChangeRegistrationAsync(test.Session.Identities[0], SignInMethod.OwnApp);
        moved.SignInMethod.Should().Be(SignInMethod.OwnApp);
    }

    [Fact]
    public async Task OnlyAnEntraAppRegistrationCanBeTheTarget()
    {
        using var test = new TestSession();
        test.Settings.ClientId = SettingsClientId;

        var act = () => test.Session.ChangeRegistrationAsync(test.Session.Identities[0], SignInMethod.AzureCLI);
        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("Only an Entra app registration");
    }

    [Fact]
    public async Task ChangingTheSettingsClientIdKeepsTheAccountsThatFollowIt()
    {
        using var test = new TestSession();
        test.Settings.ClientId = SettingsClientId;

        var (code, _, error) = await RunAsync(test, "config", "set", "client-id", OwnClientId, "--yes");

        code.Should().Be(ExitCodes.Ok);
        new CliSettings(test.Directory).ClientId.Should().Be(OwnClientId);
        var state = new Elevate.Core.Storage.AppStateStore(test.Directory).Load();
        state.Identities.Should().ContainSingle().Which.Upn.Should().Be(TestSession.Account.Upn);
        state.Tenants.Should().ContainSingle("the account keeps its tenants");
        error.Should().Contain("signs in again");
    }

    [Fact]
    public async Task ChangingTheSettingsClientIdLeavesAPinnedAccountAlone()
    {
        using var test = new TestSession();
        test.Settings.ClientId = SettingsClientId;
        Replace(test, SignInMethod.PinnedApp(OwnClientId));

        var (code, _, error) = await RunAsync(test, "config", "set", "client-id", "22222222-2222-3333-4444-555555555555", "--yes");

        code.Should().Be(ExitCodes.Ok);
        error.Should().NotContain("signs in again", "an account with its own registration does not follow the setting");
        new Elevate.Core.Storage.AppStateStore(test.Directory).Load()
            .Identities.Should().ContainSingle().Which.SignInMethod.Should().Be(SignInMethod.PinnedApp(OwnClientId));
    }

    [Fact]
    public async Task TheSettingsRegistrationNeedsAClientId()
    {
        using var test = new TestSession();
        Replace(test, SignInMethod.AzureCLI);

        var act = () => test.Session.ChangeRegistrationAsync(test.Session.Identities[0], SignInMethod.OwnApp);
        (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("No client ID is configured");
    }
}
