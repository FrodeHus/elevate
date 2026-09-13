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

        report.Summary.Should().BeEquivalentTo(new { High = 13, Medium = 5, Low = 5, Info = 2 });
        findings.Select(f => f.Id).Distinct().Should().HaveCount(10, "the sample exercises every rule");
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
}
