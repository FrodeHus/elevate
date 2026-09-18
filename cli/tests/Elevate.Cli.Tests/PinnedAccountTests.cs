using Elevate.Cli.Auth;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class PinnedAccountTests
{
    [Fact]
    public async Task PinnedAccountsAreNotAvailableAndSayWhy()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-cli-pinned").FullName;
        try
        {
            var tokens = new CliTokenProvider(new TokenCacheStore(dir, true),
                () => "11111111-2222-3333-4444-555555555555", default, _ => { });
            var pinned = SignInMethod.PinnedApp("aaaaaaaa-2222-3333-4444-555555555555");

            tokens.IsAvailable(pinned).Should().BeFalse();
            var act = () => tokens.SignInAsync(pinned);
            (await act.Should().ThrowAsync<CliException>()).Which.Message.Should().Be(SignInMethod.PinnedUnsupportedMessage);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
