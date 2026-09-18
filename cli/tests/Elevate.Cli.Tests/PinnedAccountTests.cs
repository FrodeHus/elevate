using Elevate.Cli.Auth;
using Elevate.Cli.Commands;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// An account with an Entra app registration of its own: the token provider builds a client for
/// its client id, and <c>login --method own --client-id</c> asks for one.
/// </summary>
public class PinnedAccountTests
{
    private const string SettingsClientId = "11111111-2222-3333-4444-555555555555";
    private const string OwnClientId = "aaaaaaaa-2222-3333-4444-555555555555";

    private static CliTokenProvider Provider(string directory, string settingsClientId = SettingsClientId) =>
        new(new TokenCacheStore(directory, true), () => settingsClientId, default, _ => { });

    [Fact]
    public void APinnedAccountSignsInWithItsOwnRegistration()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-cli-pinned").FullName;
        try
        {
            var tokens = Provider(dir);

            // Available without the settings client id: a pinned account carries its own.
            tokens.IsAvailable(SignInMethod.PinnedApp(OwnClientId)).Should().BeTrue();
            Provider(dir, string.Empty).IsAvailable(SignInMethod.PinnedApp(OwnClientId)).Should().BeTrue();
            tokens.IsAvailable(SignInMethod.PinnedApp("not-a-guid")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task APinnedClientIdThatIsNotAGuidIsRefused()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-cli-pinned").FullName;
        try
        {
            var act = () => Provider(dir).SignInAsync(SignInMethod.PinnedApp("not-a-guid"));
            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Contain("must be a GUID");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoginWithAClientIdOnTheOwnMethodPinsTheAccount()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-cli-pinned").FullName;
        try
        {
            var settings = new CliSettings(dir) { ClientId = SettingsClientId };

            AccountCommands.ParseMethod("own", OwnClientId, settings).Should().Be(SignInMethod.PinnedApp(OwnClientId));
            AccountCommands.ParseMethod("own", null, settings).Should().Be(SignInMethod.OwnApp);

            var act = () => AccountCommands.ParseMethod("own", "not-a-guid", settings);
            act.Should().Throw<CliException>().WithMessage("*GUID*");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AManagedClientIdRefusesARegistrationOfTheAccountsOwn()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-cli-pinned").FullName;
        try
        {
            var managed = ManagedConfiguration.Load(
                new DictionaryManagedSource(new Dictionary<string, object?> { ["ClientId"] = SettingsClientId }, "unit"));
            var settings = new CliSettings(dir, managed);

            var act = () => AccountCommands.ParseMethod("own", OwnClientId, settings);
            act.Should().Throw<CliException>().WithMessage("*managed by your organization*");

            // The managed registration itself stays reachable.
            AccountCommands.ParseMethod("own", null, settings).Should().Be(SignInMethod.OwnApp);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
