using System.Text.Json;
using Elevate.Audit.Model;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ModelTests
{
    [Fact]
    public void Snapshot_RoundTripsThroughJson()
    {
        var snapshot = Snapshot.Empty(new TenantInfo("11111111-1111-1111-1111-111111111111", "Contoso"), "alex.rivera@contoso.com", DateTimeOffset.Parse("2026-09-13T10:00:00Z")) with
        {
            Principals = [new PrincipalRecord("u1", PrincipalType.User, "Alex Rivera", "alex.rivera@contoso.com", IsGuest: false, AccountEnabled: true, ServicePrincipalType: null)],
            EntraAssignments = [new EntraAssignmentRecord("a1", "u1", "rd1", "/", null, AssignmentType.Assigned, "Direct", null, null)],
        };

        var json = JsonSerializer.Serialize(snapshot, AuditJson.Options);
        var back = JsonSerializer.Deserialize<Snapshot>(json, AuditJson.Options);

        back.Should().BeEquivalentTo(snapshot);
        json.Should().Contain("\"kind\": \"elevate-audit-snapshot\"").And.Contain("\"assignmentType\": \"assigned\"");
    }

    [Fact]
    public void Severity_SortsHighFirst()
    {
        var sorted = new[] { Severity.Low, Severity.High, Severity.Info, Severity.Medium }.OrderBy(Severities.Rank).ToArray();

        sorted.Should().Equal(Severity.High, Severity.Medium, Severity.Low, Severity.Info);
    }

    [Fact]
    public void Severity_ParsesCaseInsensitively()
    {
        Severities.Parse("high").Should().Be(Severity.High);
        Severities.Parse("MEDIUM").Should().Be(Severity.Medium);
        Severities.TryParse("urgent", out _).Should().BeFalse();
    }
}
