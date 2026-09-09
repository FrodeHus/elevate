using System.Text.Json;
using Elevate.Cli.Commands;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Tests.Support;
using Elevate.Core.Managed;
using FluentAssertions;

namespace Elevate.Cli.Tests;

/// <summary>
/// Managed (MDM/GPO) configuration as the CLI sees it: a managed client id wins over the stored
/// one and cannot be changed, <c>config</c> says where each value came from, and
/// <c>config managed</c> reports the policy in effect.
/// </summary>
[Collection(ConsoleCollection.Name)]
public class ManagedConfigTests
{
    private static ManagedConfiguration Managed(params (string Key, object? Value)[] pairs) =>
        ManagedConfiguration.Load(new DictionaryManagedSource(pairs.ToDictionary(p => p.Key, p => p.Value), "unit"));

    /// <summary>Runs the real command tree against the test session's directory, capturing both streams.</summary>
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

    [Fact]
    public void ManagedClientIdWinsAndCannotBeSet()
    {
        using var session = new TestSession(Managed(("ClientId", "11111111-2222-3333-4444-555555555555")));
        var settings = session.Settings;
        settings.ClientId.Should().Be("11111111-2222-3333-4444-555555555555");
        settings.IsClientIdManaged.Should().BeTrue();
        settings.ClientIdSource.Should().Be(SettingSource.Managed);
        settings.IsConfigured.Should().BeTrue();

        var act = () => settings.ClientId = "22222222-2222-3333-4444-555555555555";
        act.Should().Throw<InvalidOperationException>().WithMessage("client-id is managed by your organization.");
    }

    [Fact]
    public void StoredClientIdIsUsedWhenNothingIsManaged()
    {
        using var session = new TestSession();
        session.Settings.ClientIdSource.Should().Be(SettingSource.Default);
        session.Settings.ClientId = "11111111-2222-3333-4444-555555555555";
        session.Settings.ClientIdSource.Should().Be(SettingSource.User);
        session.Settings.IsClientIdManaged.Should().BeFalse();
        session.Settings.UpdateCheckDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task ConfigSetClientIdIsRefusedWhenManaged()
    {
        using var session = new TestSession(Managed(("ClientId", "11111111-2222-3333-4444-555555555555")));
        var (code, _, error) = await RunAsync(session, "config", "set", "client-id", "22222222-2222-3333-4444-555555555555", "--yes");
        code.Should().Be(ExitCodes.Usage);
        error.Should().Contain("managed by your organization");
    }

    [Fact]
    public async Task ConfigTableShowsSources()
    {
        using var session = new TestSession(Managed(("ClientId", "11111111-2222-3333-4444-555555555555")));
        var (code, output, _) = await RunAsync(session, "config");
        code.Should().Be(ExitCodes.Ok);
        output.Should().Contain("Source");
        Line(output, "client-id").Should().Contain("managed");
        Line(output, "custom-client-id").Should().Contain("default");

        var (jsonCode, json, _) = await RunAsync(session, "config", "--json");
        jsonCode.Should().Be(ExitCodes.Ok);
        var sources = JsonDocument.Parse(json).RootElement.GetProperty("sources");
        sources.GetProperty("clientId").GetString().Should().Be("managed");
        sources.GetProperty("customClientId").GetString().Should().Be("default");
        sources.GetProperty("unprotectedCache").GetString().Should().Be("default");
        sources.GetProperty("tokenHint").GetString().Should().Be("default");
    }

    [Fact]
    public async Task ConfigGetNotesTheManagedKey()
    {
        using var session = new TestSession(Managed(("ClientId", "11111111-2222-3333-4444-555555555555")));
        var (code, output, error) = await RunAsync(session, "config", "get", "client-id");
        code.Should().Be(ExitCodes.Ok);
        output.Trim().Should().Be("11111111-2222-3333-4444-555555555555");
        error.Should().Contain("client-id: managed by your organization (unit)");

        var (_, _, otherKey) = await RunAsync(session, "config", "get", "custom-client-id");
        otherKey.Should().NotContain("managed by your organization");

        var (_, _, quiet) = await RunAsync(session, "config", "get", "client-id", "--quiet");
        quiet.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigManagedListsKeysAndWarnings()
    {
        using var session = new TestSession(Managed(("ClientId", "11111111-2222-3333-4444-555555555555"), ("ManagedProfilesUrl", "http://insecure.example")));
        var (code, json, _) = await RunAsync(session, "config", "managed", "--json");
        code.Should().Be(ExitCodes.Ok);
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("origin").GetString().Should().Be("unit");
        root.GetProperty("keys").EnumerateArray().Select(e => e.GetString()).Should().Equal("ClientId");
        root.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()!).Should().ContainSingle(w => w.Contains("ManagedProfilesUrl"));

        var (tableCode, table, _) = await RunAsync(session, "config", "managed");
        tableCode.Should().Be(ExitCodes.Ok);
        table.Should().Contain("unit").And.Contain("ClientId");
    }

    [Fact]
    public async Task ConfigManagedWithoutPolicySaysSo()
    {
        using var session = new TestSession(ManagedConfiguration.None);
        var (code, _, error) = await RunAsync(session, "config", "managed");
        code.Should().Be(ExitCodes.Ok);
        error.Should().Contain("No managed configuration.");
    }

    [Fact]
    public async Task ConfigManagedFileDryRun()
    {
        using var session = new TestSession(ManagedConfiguration.None);
        var path = Path.Combine(session.Directory, "managed.json");
        await File.WriteAllTextAsync(path, """{"ClientId":"33333333-2222-3333-4444-555555555555","DisableUpdateCheck":true}""");

        var (code, json, error) = await RunAsync(session, "config", "managed", "--file", path, "--json");
        code.Should().Be(ExitCodes.Ok);
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("origin").GetString().Should().Be(path);
        root.GetProperty("keys").EnumerateArray().Select(e => e.GetString()).Should().Equal("ClientId", "DisableUpdateCheck");
        error.Should().Contain("ownership check is skipped");

        var (missingCode, _, missingError) = await RunAsync(session, "config", "managed", "--file", Path.Combine(session.Directory, "absent.json"));
        missingCode.Should().Be(ExitCodes.Ok);
        missingError.Should().Contain("No managed configuration.");
    }

    [Fact]
    public void UpdateCheckDisabledSkipsTheMention()
    {
        using var managed = new TestSession(Managed(("DisableUpdateCheck", true)));
        using var plain = new TestSession();
        var output = new Output(json: false, quiet: false, noColor: true);
        MiscCommands.ShouldMentionUpdate(output, managed.Settings).Should().BeFalse();
        MiscCommands.ShouldMentionUpdate(output, plain.Settings).Should().BeTrue();
        MiscCommands.ShouldMentionUpdate(new Output(json: true, quiet: false, noColor: true), plain.Settings).Should().BeFalse();
        MiscCommands.ShouldMentionUpdate(new Output(json: false, quiet: true, noColor: true), plain.Settings).Should().BeFalse();
    }

    /// <summary>The rendered table line for a key, with the box drawing and padding taken out.</summary>
    private static string Line(string output, string key) =>
        output.Split('\n').FirstOrDefault(l => l.Contains(key + " ", StringComparison.Ordinal)) ?? string.Empty;
}
