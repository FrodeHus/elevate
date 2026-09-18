using System.Text.Json;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class JsonAndSnapshotTests
{
    [Fact]
    public void Report_FromSample_MatchesTheGolden()
    {
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions();
        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, findings, findings, options, "sample", Elevate.Audit.Auth.ClientIds.GraphReadScopeNames);

        var json = JsonRenderer.Render(report);

        report.Summary.Should().BeEquivalentTo(new { High = 14, Medium = 9, Low = 5, Info = 2 });
        findings.Select(f => f.Id).Distinct().Should().HaveCount(13, "the sample exercises every rule");
        Golden.Check("audit/tests/Elevate.Audit.Tests/Golden/sample-report.json", json);
    }

    [Fact]
    public void SampleSnapshot_MatchesItsCommittedJson()
    {
        Golden.Check("audit/tests/Elevate.Audit.Tests/Fixtures/snapshots/sample.json", JsonSerializer.Serialize(SampleSnapshot.Build(), AuditJson.Options));
    }

    [Fact]
    public void SnapshotFile_RoundTrips_AndRefusesAReport()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-audit-tests");
        var snapshotPath = Path.Combine(dir.FullName, "snapshot.json");
        var reportPath = Path.Combine(dir.FullName, "report.json");
        var snapshot = SampleSnapshot.Build();

        SnapshotFile.Save(snapshot, snapshotPath);
        File.WriteAllText(reportPath, JsonRenderer.Render(AuditReport.From(snapshot, [], [], new AuditOptions(), "x", [])));

        SnapshotFile.Load(snapshotPath).Should().BeEquivalentTo(snapshot, o => o.Excluding(ctx => ctx.Path.EndsWith("IsPermanent", StringComparison.Ordinal)));
        var act = () => SnapshotFile.Load(reportPath);
        act.Should().Throw<AuditException>().WithMessage("*report, not a snapshot*");
        var missing = () => SnapshotFile.Load(Path.Combine(dir.FullName, "nope.json"));
        missing.Should().Throw<AuditException>().WithMessage("*not found*");
    }

    [Fact]
    public void AllFindings_CarriesEveryFinding_AndIsNotSerialised()
    {
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions(MinSeverity: Severity.High);
        var all = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, all, RuleRunner.Visible(all, options), options, "x", []);

        report.AllFindings.Should().BeSameAs(all);
        report.Findings.Count.Should().BeLessThan(all.Count);
        System.Text.Json.JsonSerializer.Serialize(report, AuditJson.Options).Should().NotContain("allFindings");
    }
}
