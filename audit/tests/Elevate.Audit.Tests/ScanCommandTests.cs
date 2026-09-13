using System.Text.Json;
using Elevate.Audit;
using Elevate.Audit.Rendering;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

[Collection("console")]
public class ScanCommandTests
{
    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (previousOut, previousErr) = (Console.Out, Console.Error);
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await Program.Main(args);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }
    }

    private static string SnapshotPath()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("elevate-audit").FullName, "snapshot.json");
        SnapshotFile.Save(SampleSnapshot.Build(), path);
        return path;
    }

    [Fact]
    public async Task FromSnapshot_JsonToStdout_ExitsTwoOnHighFindings()
    {
        var (code, stdout, _) = await RunAsync("--from-snapshot", SnapshotPath(), "--json", "-", "--quiet");

        code.Should().Be(2);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("summary").GetProperty("high").GetInt32().Should().Be(13);
        doc.RootElement.GetProperty("tool").GetProperty("name").GetString().Should().Be("elevate-audit");
    }

    [Fact]
    public async Task NoFail_AndMinSeverity_AndIgnore_AreHonoured()
    {
        var (code, stdout, _) = await RunAsync("--from-snapshot", SnapshotPath(), "--json", "-", "--no-fail", "--min-severity", "medium", "--ignore", "GA-COUNT", "--ignore", "sp-permanent");

        code.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout);
        var ids = doc.RootElement.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToList();
        ids.Should().NotContain("GA-COUNT").And.NotContain("SP-PERMANENT").And.NotContain("ELIGIBLE-NO-END");
        doc.RootElement.GetProperty("options").GetProperty("ignored").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task MinSeverity_FiltersFindingsOnly_TheSummaryStaysComplete()
    {
        var (code, stdout, _) = await RunAsync("--from-snapshot", SnapshotPath(), "--json", "-", "--no-fail", "--min-severity", "medium");

        code.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout);
        var summary = doc.RootElement.GetProperty("summary");
        summary.GetProperty("high").GetInt32().Should().Be(13);
        summary.GetProperty("medium").GetInt32().Should().Be(5);
        summary.GetProperty("low").GetInt32().Should().Be(5);
        summary.GetProperty("info").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("hidden").GetInt32().Should().Be(7, "the 5 low and 2 info findings are hidden by --min-severity medium");
        var findings = doc.RootElement.GetProperty("findings").EnumerateArray().ToList();
        findings.Should().HaveCount(18);
        findings.Select(f => f.GetProperty("severity").GetString()).Should().NotContain("low").And.NotContain("info");
    }

    [Fact]
    public async Task HtmlAndJsonFiles_AreWritten_AndTerminalStillPrints()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-audit").FullName;
        var html = Path.Combine(dir, "report.html");
        var json = Path.Combine(dir, "report.json");

        var (code, stdout, _) = await RunAsync("--from-snapshot", SnapshotPath(), "--html", html, "--json", json, "--no-color");

        code.Should().Be(2);
        File.ReadAllText(html).Should().StartWith("<!doctype html>");
        File.ReadAllText(json).Should().Contain("\"findings\"");
        stdout.Should().Contain("ENTRA-USER-PERMANENT").And.Contain("13 high");
    }

    [Fact]
    public async Task BadInputs_ExitOneWithOneLine()
    {
        var (code, _, stderr) = await RunAsync("--from-snapshot", Path.Combine(Path.GetTempPath(), "does-not-exist.json"));
        code.Should().Be(1);
        stderr.Trim().Should().Contain("not found").And.NotContain("   at ");

        var (code2, _, stderr2) = await RunAsync("--from-snapshot", SnapshotPath(), "--min-severity", "urgent");
        code2.Should().Be(1);
        stderr2.Should().Contain("Unknown severity");
    }

    [Fact]
    public async Task SaveSnapshot_WritesAReloadableFile()
    {
        var output = Path.Combine(Directory.CreateTempSubdirectory("elevate-audit").FullName, "again.json");

        await RunAsync("--from-snapshot", SnapshotPath(), "--save-snapshot", output, "--quiet", "--no-fail");

        SnapshotFile.Load(output).Tenant.DisplayName.Should().Be("Contoso");
    }
}

[CollectionDefinition("console", DisableParallelization = true)]
public class ConsoleCollection;
