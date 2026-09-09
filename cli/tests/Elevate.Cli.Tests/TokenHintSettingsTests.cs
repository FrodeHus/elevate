using System.Text.Json.Nodes;
using Elevate.Cli.Infrastructure;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class TokenHintSettingsTests
{
    [Fact]
    public void DismissedAccountsRoundTripAndLeaveOtherKeysAlone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{"panelTab":"azure","clientId":"11111111-2222-3333-4444-555555555555"}""");
            var settings = new CliSettings(dir);
            settings.DismissedTokenHintAccounts.Should().BeEmpty();

            settings.DismissedTokenHintAccounts = new HashSet<string> { "id2", "id1" };

            new CliSettings(dir).DismissedTokenHintAccounts.Should().BeEquivalentTo(["id1", "id2"]);
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.AsObject();
            json["panelTab"]!.GetValue<string>().Should().Be("azure", "the Windows app's keys survive");
            json["dismissedTokenHintAccounts"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("id1", "id2");

            settings.DismissedTokenHintAccounts = new HashSet<string>();
            JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.AsObject().ContainsKey("dismissedTokenHintAccounts").Should().BeFalse("an empty set is not written");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
