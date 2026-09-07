using System.Text.Json.Nodes;
using Elevate.Cli.Infrastructure;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class SettingsAndDirectoryTests
{
    [Fact]
    public void SettingsKeepUnknownKeysAndRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"clientId":"","panelTab":"azure","hotKey":{"key":65},"unprotectedCache":true}""");

        var settings = new CliSettings(dir);
        settings.IsConfigured.Should().BeFalse();
        settings.UnprotectedCache.Should().BeTrue();
        settings.ClientId = "11111111-2222-3333-4444-555555555555";
        settings.CustomClientId = " 22222222-2222-3333-4444-555555555555 ";

        var reloaded = new CliSettings(dir);
        reloaded.ClientId.Should().Be("11111111-2222-3333-4444-555555555555");
        reloaded.CustomClientId.Should().Be("22222222-2222-3333-4444-555555555555");
        reloaded.IsConfigured.Should().BeTrue();
        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!.AsObject();
        raw["panelTab"]!.GetValue<string>().Should().Be("azure", "the desktop app's keys survive");
        raw["hotKey"]!["key"]!.GetValue<int>().Should().Be(65);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void CorruptSettingsStartEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{not json");
        var settings = new CliSettings(dir);
        settings.ClientId.Should().BeEmpty();
        Directory.Delete(dir, recursive: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("abc")]
    public void InvalidClientIds(string value) => CliSettings.IsValidClientId(value).Should().BeFalse();

    [Fact]
    public void DataDirectoryOverrideWins()
    {
        var explicitDir = Path.Combine(Path.GetTempPath(), "explicit");
        DataDirectory.Resolve(explicitDir).Should().Be(Path.GetFullPath(explicitDir));
    }

    [Fact]
    public void DataDirectoryDefaultEndsWithProductName()
    {
        var previous = Environment.GetEnvironmentVariable(DataDirectory.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, null);
            Path.GetFileName(DataDirectory.Resolve(null)).Should().Be("elevate-cli");
            Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, Path.Combine(Path.GetTempPath(), "from-env"));
            Path.GetFileName(DataDirectory.Resolve(null)).Should().Be("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DataDirectory.EnvironmentVariable, previous);
        }
    }
}
