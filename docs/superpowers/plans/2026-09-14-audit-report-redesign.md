# Audit Report Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the `--html` audit report around an executive summary, collapsible areas, per-group roll-ups with a nesting outline and diagram, and a small inline script for search, filters and caps.

**Architecture:** Two new pure classes under `audit/src/Elevate.Audit/Rendering/` do the thinking: `ReportAreas` (rule → area, tile state, sentences, verdict) and `GroupRollup` (cards, membership trie, outline HTML, SVG diagram). `HtmlRenderer` is rewritten to call them and emit the new structure; `report.css` and a new embedded `report.js` carry the styling and the progressive enhancement. Terminal and JSON output do not change; `AuditReport` gains one `[JsonIgnore]` property so the renderer can see unfiltered findings.

**Tech Stack:** .NET 10 / C# 13, xunit 2.9 + FluentAssertions 7, golden files via `Tests/Support/Golden.cs`, vanilla ES5 JavaScript, plain CSS.

**Spec:** `docs/superpowers/specs/2026-09-14-audit-report-redesign-design.md`

## Global Constraints

- The JSON report's field names are a stability contract (golden test `Golden/sample-report.json`); do not add serialised fields.
- The HTML is one self-contained file: no external requests, no `<link>`, no `<img>`, no `<script src`; links are `https://` or `#` fragments; exactly one `<h1>`.
- The `:root` token block in `report.css` must stay byte-identical to `site/styles.css`'s (existing test).
- The page must be complete with JavaScript off: the script only hides, reveals, opens, closes, and inserts the toolbar. The rendered markup carries no `hidden` attribute.
- Rule-block ids keep the form `<rule-code-lowercase>-<severity>` (existing tests pin `azure-permanent-high` and `azure-permanent-medium`).
- Every user-supplied string goes through the HTML encoder `E(...)`.
- Run tests from the repo root: `dotnet test audit/Elevate.Audit.sln`. Regenerate goldens with `ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln` and review the diff before committing.
- Commit messages follow the repo style: `Audit: <what changed>`; no attribution lines.

---

## File map

| File | Responsibility |
|---|---|
| `audit/src/Elevate.Audit/Rendering/ReportAreas.cs` (new) | Area catalogue, rule → area, skipped-source effects, tile state, tile sentences, verdict. Pure. |
| `audit/src/Elevate.Audit/Rendering/GroupRollup.cs` (new) | Roll-up cards from findings, the membership trie, outline HTML, SVG diagram. Pure. |
| `audit/src/Elevate.Audit/Rendering/AuditReport.cs` | Add `AllFindings` (`[JsonIgnore]`). |
| `audit/src/Elevate.Audit/Rendering/HtmlRenderer.cs` | Rewritten page structure. |
| `audit/src/Elevate.Audit/Rendering/report.css` | New components; tokens unchanged. |
| `audit/src/Elevate.Audit/Rendering/report.js` (new, embedded) | Toolbar, search, severity chips, caps, anchors, expand/collapse, print. |
| `audit/src/Elevate.Audit/Elevate.Audit.csproj` | Embed `report.js`. |
| `audit/tests/Elevate.Audit.Tests/ReportAreasTests.cs` (new) | |
| `audit/tests/Elevate.Audit.Tests/GroupRollupTests.cs` (new) | |
| `audit/tests/Elevate.Audit.Tests/HtmlRendererTests.cs` | Updated and extended. |
| `audit/tests/Elevate.Audit.Tests/Support/SampleSnapshot.cs` | Deeper nesting in the sample. |
| `site/audit-sample.html`, `audit/tests/Elevate.Audit.Tests/Golden/sample-report.json` | Regenerated goldens. |
| `docs/audit.md` §6 | Rewritten for the new structure. |
| `docs/superpowers/specs/2026-09-14-audit-report-redesign-design.md` §5 | Two small amendments (see Task 3). |

---

### Task 1: `ReportAreas` — catalogue, rule mapping, skipped-source effects, tile state

**Files:**
- Create: `audit/src/Elevate.Audit/Rendering/ReportAreas.cs`
- Test: `audit/tests/Elevate.Audit.Tests/ReportAreasTests.cs`

**Interfaces:**
- Produces: `enum AreaState { Critical, Attention, Review, Clean, NotScanned }`; `record ReportArea(string Id, string Name, string Description)`; static `ReportAreas.Entra/PimGroups/Azure/Guests/Workload/Hygiene/Other/Coverage`, `ReportAreas.Ordered`, `ReportAreas.Of(string ruleCode)`, `ReportAreas.NotScannedReason(ReportArea, IReadOnlyList<SkippedSource>)`, `ReportAreas.UnderCounts(ReportArea, IReadOnlyList<SkippedSource>)`, `ReportAreas.StateOf(ReportArea, IReadOnlyList<Finding>, IReadOnlyList<SkippedSource>)`, `ReportAreas.StateLabel(AreaState)`, `ReportAreas.Plural(int, string, string)`.

- [ ] **Step 1: Write the failing tests**

Create `audit/tests/Elevate.Audit.Tests/ReportAreasTests.cs`:

```csharp
using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ReportAreasTests
{
    private static Finding F(string code, Severity severity, PrincipalType type = PrincipalType.User, string principalId = "p1", IReadOnlyList<GroupRef>? via = null) =>
        new(code, severity, new FindingPrincipal(principalId, "Name " + principalId, null, type, false, true),
            new FindingRole("r1", "Global Administrator", null, true, RoleSystem.Entra), new FindingScope("/", "Directory", ScopeKind.Directory),
            via ?? [], "remedy", "https://portal.azure.com/", new FindingEvidence("a1", null, null, AssignmentType.Assigned, null));

    [Theory]
    [InlineData("ENTRA-USER-PERMANENT", "entra")]
    [InlineData("ENTRA-GROUP-PERMANENT", "entra")]
    [InlineData("ENTRA-GROUP-NOT-PIM", "entra")]
    [InlineData("ENTRA-GROUP-NOT-ASSIGNABLE", "entra")]
    [InlineData("GROUP-MEMBER-PERMANENT", "pim-groups")]
    [InlineData("AZURE-PERMANENT", "azure")]
    [InlineData("GUEST-PERMANENT", "guests")]
    [InlineData("SP-PERMANENT", "workload")]
    [InlineData("ELIGIBLE-NO-END", "hygiene")]
    [InlineData("GA-COUNT", "hygiene")]
    [InlineData("FUTURE-RULE", "other")]
    public void Of_MapsEveryRuleCode(string code, string areaId)
    {
        ReportAreas.Of(code).Id.Should().Be(areaId);
    }

    [Fact]
    public void EveryShippedRule_HasAnAreaOtherThanOther()
    {
        RuleRunner.All.Select(r => ReportAreas.Of(r.Code)).Should().NotContain(ReportAreas.Other);
    }

    [Fact]
    public void Ordered_ListsFindingAreasInReportOrder_WithoutCoverage()
    {
        ReportAreas.Ordered.Select(a => a.Id).Should().Equal("entra", "pim-groups", "azure", "guests", "workload", "hygiene", "other");
    }

    [Fact]
    public void StateOf_HighWins_ThenMediumOrLow_ThenInfo_ThenClean()
    {
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.High), F("X", Severity.Info)], []).Should().Be(AreaState.Critical);
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.Medium)], []).Should().Be(AreaState.Attention);
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.Low)], []).Should().Be(AreaState.Attention);
        ReportAreas.StateOf(ReportAreas.Entra, [F("X", Severity.Info)], []).Should().Be(AreaState.Review);
        ReportAreas.StateOf(ReportAreas.Entra, [], []).Should().Be(AreaState.Clean);
    }

    [Fact]
    public void StateOf_NotScanned_WinsOverFindings()
    {
        var skipped = new[] { new SkippedSource("azure", "skipped with --skip-azure") };
        ReportAreas.StateOf(ReportAreas.Azure, [F("AZURE-PERMANENT", Severity.High)], skipped).Should().Be(AreaState.NotScanned);
        ReportAreas.NotScannedReason(ReportAreas.Azure, skipped).Should().Be("skipped with --skip-azure");
        ReportAreas.StateOf(ReportAreas.PimGroups, [], [new SkippedSource("pim-for-groups", "403")]).Should().Be(AreaState.NotScanned);
        ReportAreas.StateOf(ReportAreas.Entra, [], skipped).Should().Be(AreaState.Clean);
    }

    [Fact]
    public void UnderCounts_ManagementGroupsHitAzure_GroupsHitEntraAndGuests()
    {
        var mg = new[] { new SkippedSource("azure-management-groups", "not readable") };
        var groups = new[] { new SkippedSource("groups", "2 nested group(s) could not be read; their members are not included.") };
        ReportAreas.UnderCounts(ReportAreas.Azure, mg).Should().BeTrue();
        ReportAreas.UnderCounts(ReportAreas.Entra, mg).Should().BeFalse();
        ReportAreas.UnderCounts(ReportAreas.Entra, groups).Should().BeTrue();
        ReportAreas.UnderCounts(ReportAreas.Guests, groups).Should().BeTrue();
        ReportAreas.UnderCounts(ReportAreas.Azure, groups).Should().BeFalse();
        ReportAreas.UnderCounts(ReportAreas.Entra, [new SkippedSource("principals", "x")]).Should().BeFalse();
    }

    [Fact]
    public void StateLabel_And_Plural()
    {
        ReportAreas.StateLabel(AreaState.NotScanned).Should().Be("Not scanned");
        ReportAreas.StateLabel(AreaState.Critical).Should().Be("Critical");
        ReportAreas.Plural(1, "person", "people").Should().Be("1 person");
        ReportAreas.Plural(3, "person", "people").Should().Be("3 people");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~ReportAreasTests"`
Expected: build error, `ReportAreas` does not exist.

- [ ] **Step 3: Write the implementation**

Create `audit/src/Elevate.Audit/Rendering/ReportAreas.cs`:

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public enum AreaState { Critical, Attention, Review, Clean, NotScanned }

public sealed record ReportArea(string Id, string Name, string Description);

/// <summary>
/// The HTML report's areas: which area a rule belongs to, how skipped sources affect each area, and how a
/// summary tile reads. Pure functions over findings; the renderer only formats what these return.
/// </summary>
public static class ReportAreas
{
    public static readonly ReportArea Entra = new("entra", "Entra roles", "Permanent active directory role assignments, held directly or through groups.");
    public static readonly ReportArea PimGroups = new("pim-groups", "PIM for Groups", "Groups PIM already manages that still have permanent members or owners.");
    public static readonly ReportArea Azure = new("azure", "Azure RBAC", "Permanent privileged Azure role assignments at any scope.");
    public static readonly ReportArea Guests = new("guests", "Guests", "Guests holding a permanent privileged role, directly or through a group.");
    public static readonly ReportArea Workload = new("workload", "Workload identities", "Service principals and managed identities holding permanent privileged roles; PIM eligibility does not apply.");
    public static readonly ReportArea Hygiene = new("hygiene", "Hygiene", "Eligibilities without an end date and the Global Administrator count.");
    public static readonly ReportArea Other = new("other", "Other", "Findings from rules this renderer does not know.");
    public static readonly ReportArea Coverage = new("coverage", "Coverage", "Which sources were read and which were skipped.");

    /// <summary>Finding areas in report order. Coverage is rendered after them, from the skipped list.</summary>
    public static IReadOnlyList<ReportArea> Ordered { get; } = [Entra, PimGroups, Azure, Guests, Workload, Hygiene, Other];

    private static readonly Dictionary<string, ReportArea> ByRule = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTRA-USER-PERMANENT"] = Entra,
        ["ENTRA-GROUP-PERMANENT"] = Entra,
        ["ENTRA-GROUP-NOT-PIM"] = Entra,
        ["ENTRA-GROUP-NOT-ASSIGNABLE"] = Entra,
        ["GROUP-MEMBER-PERMANENT"] = PimGroups,
        ["AZURE-PERMANENT"] = Azure,
        ["GUEST-PERMANENT"] = Guests,
        ["SP-PERMANENT"] = Workload,
        ["ELIGIBLE-NO-END"] = Hygiene,
        ["GA-COUNT"] = Hygiene,
    };

    /// <summary>The area a rule code renders under; an unknown code lands in <see cref="Other"/> so a new rule never disappears.</summary>
    public static ReportArea Of(string ruleCode)
    {
        ArgumentNullException.ThrowIfNull(ruleCode);
        return ByRule.TryGetValue(ruleCode, out var area) ? area : Other;
    }

    /// <summary>The skipped reason when the area's whole source was skipped (Azure with <c>azure</c>, PIM for Groups with <c>pim-for-groups</c>); otherwise null.</summary>
    public static string? NotScannedReason(ReportArea area, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(skipped);
        var source = area == Azure ? "azure" : area == PimGroups ? "pim-for-groups" : null;
        return source is null ? null : skipped.FirstOrDefault(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase))?.Reason;
    }

    /// <summary>True when a partially skipped source means the area under-reports: management groups for Azure, unreadable groups for Entra and Guests.</summary>
    public static bool UnderCounts(ReportArea area, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(skipped);
        return (area == Azure && Has(skipped, "azure-management-groups")) || ((area == Entra || area == Guests) && Has(skipped, "groups"));
    }

    public static AreaState StateOf(ReportArea area, IReadOnlyList<Finding> areaFindings, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(areaFindings);
        if (NotScannedReason(area, skipped) is not null)
        {
            return AreaState.NotScanned;
        }

        if (areaFindings.Any(f => f.Severity == Severity.High))
        {
            return AreaState.Critical;
        }

        if (areaFindings.Any(f => f.Severity is Severity.Medium or Severity.Low))
        {
            return AreaState.Attention;
        }

        return areaFindings.Count > 0 ? AreaState.Review : AreaState.Clean;
    }

    public static string StateLabel(AreaState state) => state switch
    {
        AreaState.Critical => "Critical",
        AreaState.Attention => "Attention",
        AreaState.Review => "Review",
        AreaState.Clean => "Clean",
        _ => "Not scanned",
    };

    /// <summary>"1 person" / "3 people".</summary>
    public static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    internal static bool Has(IReadOnlyList<SkippedSource> skipped, string source) =>
        skipped.Any(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~ReportAreasTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add audit/src/Elevate.Audit/Rendering/ReportAreas.cs audit/tests/Elevate.Audit.Tests/ReportAreasTests.cs
git commit -m "Audit: report areas — rule mapping, skipped-source effects, tile state"
```

---

### Task 2: `ReportAreas` — tile sentences and the verdict

**Files:**
- Modify: `audit/src/Elevate.Audit/Rendering/ReportAreas.cs`
- Test: `audit/tests/Elevate.Audit.Tests/ReportAreasTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `record Verdict(string Head, string? Tail)`; `ReportAreas.Sentence(ReportArea area, IReadOnlyList<Finding> areaFindings, IReadOnlyList<SkippedSource> skipped)`; `ReportAreas.VerdictFor(string tenantName, IReadOnlyList<Finding> allFindings, IReadOnlyList<SkippedSource> skipped)`; `ReportAreas.SkippedClause(string source)`.

- [ ] **Step 1: Write the failing tests**

Append to the class in `ReportAreasTests.cs`:

```csharp
    private static readonly GroupRef Top = new("g1", "Tier 0 Admins");

    [Fact]
    public void Sentence_Entra_CountsDirectPeopleAndGroupGrants()
    {
        var f = new[]
        {
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u1"),
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u1"), // same person, two roles
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u2"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, PrincipalType.Group, "g1"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u3", via: [Top]),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u4", via: [Top]),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u4", via: [Top]),
        };
        ReportAreas.Sentence(ReportAreas.Entra, f, []).Should().Be("2 people hold a permanent role directly. 1 group grants roles to 2 more people.");
        ReportAreas.Sentence(ReportAreas.Entra, f.Take(3).ToList(), []).Should().Be("2 people hold a permanent role directly.");
        ReportAreas.Sentence(ReportAreas.Entra, f.Skip(3).ToList(), []).Should().Be("1 group grants roles to 2 people.");
        ReportAreas.Sentence(ReportAreas.Entra, [], []).Should().Be("No permanent Entra role assignments.");
        ReportAreas.Sentence(ReportAreas.Entra, [F("ENTRA-GROUP-NOT-ASSIGNABLE", Severity.Medium, PrincipalType.Group, "g9")], []).Should().Be("1 finding to review.");
    }

    [Fact]
    public void Sentence_OtherAreas()
    {
        ReportAreas.Sentence(ReportAreas.PimGroups, [F("GROUP-MEMBER-PERMANENT", Severity.High, principalId: "u1"), F("GROUP-MEMBER-PERMANENT", Severity.High, principalId: "u2")], [])
            .Should().Be("2 permanent members remain in groups PIM already manages.");
        ReportAreas.Sentence(ReportAreas.PimGroups, [], []).Should().Be("Every PIM-managed group has only eligible members.");

        var azure = new[]
        {
            F("AZURE-PERMANENT", Severity.High, principalId: "u1") with { Scope = new FindingScope("/subscriptions/a", "Prod", ScopeKind.Subscription) },
            F("AZURE-PERMANENT", Severity.High, principalId: "u2") with { Scope = new FindingScope("/subscriptions/a", "Prod", ScopeKind.Subscription) },
            F("AZURE-PERMANENT", Severity.Medium, principalId: "u3") with { Scope = new FindingScope("/subscriptions/b", "Dev", ScopeKind.Subscription) },
            F("AZURE-PERMANENT", Severity.High, principalId: "u4", via: [Top]) with { Scope = new FindingScope("/subscriptions/b", "Dev", ScopeKind.Subscription) },
        };
        ReportAreas.Sentence(ReportAreas.Azure, azure, []).Should().Be("3 permanent privileged assignments across 2 scopes.");
        ReportAreas.Sentence(ReportAreas.Azure, [], []).Should().Be("No permanent privileged Azure assignments.");

        ReportAreas.Sentence(ReportAreas.Guests, [F("GUEST-PERMANENT", Severity.High, principalId: "u1")], []).Should().Be("1 guest holds a permanent privileged role.");
        ReportAreas.Sentence(ReportAreas.Guests, [], []).Should().Be("No guest holds a permanent privileged role.");

        ReportAreas.Sentence(ReportAreas.Workload, [F("SP-PERMANENT", Severity.Info, PrincipalType.ServicePrincipal, "sp1"), F("SP-PERMANENT", Severity.Info, PrincipalType.ServicePrincipal, "sp2")], [])
            .Should().Be("2 service principals hold permanent roles. Review whether they need them.");
        ReportAreas.Sentence(ReportAreas.Workload, [], []).Should().Be("No service principal holds a permanent privileged role.");

        var ga = F("GA-COUNT", Severity.Medium, PrincipalType.Unknown, "tenant") with { Remedy = "7 principals can become Global Administrator (permanent or eligible). Microsoft recommends at most five; move the rest to narrower roles." };
        ReportAreas.Sentence(ReportAreas.Hygiene, [F("ELIGIBLE-NO-END", Severity.Low), F("ELIGIBLE-NO-END", Severity.Low), ga], [])
            .Should().Be("2 eligibilities never expire. 7 principals can become Global Administrator (permanent or eligible).");
        ReportAreas.Sentence(ReportAreas.Hygiene, [ga], []).Should().Be("7 principals can become Global Administrator (permanent or eligible).");
        ReportAreas.Sentence(ReportAreas.Hygiene, [], []).Should().Be("Eligibilities expire and the Global Administrator count is within range.");

        ReportAreas.Sentence(ReportAreas.Other, [F("FUTURE", Severity.Low)], []).Should().Be("1 finding to review.");
    }

    [Fact]
    public void Sentence_Coverage_AndNotScanned()
    {
        ReportAreas.Sentence(ReportAreas.Coverage, [], []).Should().Be("Every source was read.");
        ReportAreas.Sentence(ReportAreas.Coverage, [], [new SkippedSource("azure-management-groups", "Not readable."), new SkippedSource("groups", "2 nested group(s) could not be read.")])
            .Should().Be("Not readable. 2 nested group(s) could not be read.");
        ReportAreas.Sentence(ReportAreas.Azure, [], [new SkippedSource("azure", "skipped with --skip-azure")]).Should().Be("skipped with --skip-azure");
    }

    [Fact]
    public void Verdict_CountsDistinctPeopleAndWorkloadIdentities_AcrossStandingRulesOnly()
    {
        var f = new[]
        {
            F("ENTRA-USER-PERMANENT", Severity.High, principalId: "u1"),
            F("AZURE-PERMANENT", Severity.High, principalId: "u1"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, PrincipalType.Group, "g1"),
            F("ENTRA-GROUP-PERMANENT", Severity.High, principalId: "u2", via: [Top]),
            F("GUEST-PERMANENT", Severity.High, principalId: "u2", via: [Top]),
            F("SP-PERMANENT", Severity.Info, PrincipalType.ServicePrincipal, "sp1"),
            F("AZURE-PERMANENT", Severity.High, PrincipalType.ServicePrincipal, "sp1"),
            F("ELIGIBLE-NO-END", Severity.Low, principalId: "u9"),
            F("GA-COUNT", Severity.Medium, PrincipalType.Unknown, "tenant"),
        };
        var v = ReportAreas.VerdictFor("Contoso", f, []);
        v.Head.Should().Be("2 people and 1 workload identity hold standing privileged access in Contoso.");
        v.Tail.Should().BeNull();

        ReportAreas.VerdictFor("Contoso", f.Take(4).ToList(), []).Head.Should().Be("2 people hold standing privileged access in Contoso.");
        ReportAreas.VerdictFor("Contoso", [f[0]], []).Head.Should().Be("1 person holds standing privileged access in Contoso.");
        ReportAreas.VerdictFor("Contoso", [f[5]], []).Head.Should().Be("1 workload identity holds standing privileged access in Contoso.");
        ReportAreas.VerdictFor("Contoso", [f[7], f[8]], []).Head.Should().Be("No standing privileged access was found in Contoso.");
    }

    [Fact]
    public void Verdict_Tail_OneClausePerDistinctSkippedSource()
    {
        var skipped = new[]
        {
            new SkippedSource("azure-management-groups", "a"),
            new SkippedSource("groups", "b"),
            new SkippedSource("groups", "c"),
            new SkippedSource("weird", "d"),
        };
        ReportAreas.VerdictFor("Contoso", [], skipped).Tail.Should()
            .Be("Azure management groups were not scanned, so the Azure section under-counts; some groups could not be read, so the Entra and Guests sections under-count; the weird source was skipped.");
        ReportAreas.VerdictFor("Contoso", [], [new SkippedSource("azure", "x")]).Tail.Should().Be("Azure was not scanned.");
        ReportAreas.SkippedClause("pim-for-groups").Should().Be("PIM for Groups was not scanned");
        ReportAreas.SkippedClause("principals").Should().Be("some principals could not be resolved to names");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~ReportAreasTests"`
Expected: build error, `Sentence`, `VerdictFor`, `Verdict` missing.

- [ ] **Step 3: Write the implementation**

Add to `ReportAreas.cs`, before the final `}` of the class, and add the `Verdict` record after the `ReportArea` record:

```csharp
public sealed record Verdict(string Head, string? Tail);
```

```csharp
    private static readonly HashSet<string> StandingRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENTRA-USER-PERMANENT", "ENTRA-GROUP-PERMANENT", "GROUP-MEMBER-PERMANENT", "AZURE-PERMANENT", "GUEST-PERMANENT", "SP-PERMANENT",
    };

    /// <summary>The one-line sentence under a tile. <paramref name="areaFindings"/> is every finding of the area (unfiltered).</summary>
    public static string Sentence(ReportArea area, IReadOnlyList<Finding> areaFindings, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(areaFindings);
        ArgumentNullException.ThrowIfNull(skipped);
        if (area == Coverage)
        {
            return skipped.Count == 0 ? "Every source was read." : string.Join(" ", skipped.Select(s => s.Reason));
        }

        if (NotScannedReason(area, skipped) is { } reason)
        {
            return reason;
        }

        if (area == Entra)
        {
            var direct = Distinct(areaFindings.Where(f => Is(f, "ENTRA-USER-PERMANENT")));
            var groups = Distinct(areaFindings.Where(f => Is(f, "ENTRA-GROUP-PERMANENT") && f.Principal.Type == PrincipalType.Group));
            var through = Distinct(areaFindings.Where(f => Is(f, "ENTRA-GROUP-PERMANENT") && f.Via.Count > 0));
            var parts = new List<string>();
            if (direct > 0)
            {
                parts.Add($"{Plural(direct, "person holds", "people hold")} a permanent role directly.");
            }

            if (groups > 0)
            {
                parts.Add($"{Plural(groups, "group grants", "groups grant")} roles to {(direct > 0 ? Plural(through, "more person", "more people") : Plural(through, "person", "people"))}.");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : areaFindings.Count == 0 ? "No permanent Entra role assignments." : Review(areaFindings.Count);
        }

        if (area == PimGroups)
        {
            var n = Distinct(areaFindings);
            return n == 0 ? "Every PIM-managed group has only eligible members." : $"{Plural(n, "permanent member remains", "permanent members remain")} in groups PIM already manages.";
        }

        if (area == Azure)
        {
            var assignments = areaFindings.Where(f => f.Via.Count == 0).ToList();
            if (assignments.Count == 0)
            {
                return "No permanent privileged Azure assignments.";
            }

            var scopes = assignments.Select(f => f.Scope.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return $"{Plural(assignments.Count, "permanent privileged assignment", "permanent privileged assignments")} across {Plural(scopes, "scope", "scopes")}.";
        }

        if (area == Guests)
        {
            var n = Distinct(areaFindings);
            return n == 0 ? "No guest holds a permanent privileged role." : $"{Plural(n, "guest holds", "guests hold")} a permanent privileged role.";
        }

        if (area == Workload)
        {
            var n = Distinct(areaFindings);
            return n == 0 ? "No service principal holds a permanent privileged role." : $"{Plural(n, "service principal holds", "service principals hold")} permanent roles. Review whether they need them.";
        }

        if (area == Hygiene)
        {
            var noEnd = areaFindings.Count(f => Is(f, "ELIGIBLE-NO-END"));
            var ga = areaFindings.FirstOrDefault(f => Is(f, "GA-COUNT"));
            var parts = new List<string>();
            if (noEnd > 0)
            {
                parts.Add($"{Plural(noEnd, "eligibility never expires", "eligibilities never expire")}.");
            }

            if (ga is not null)
            {
                parts.Add(FirstSentence(ga.Remedy));
            }

            return parts.Count > 0 ? string.Join(" ", parts) : "Eligibilities expire and the Global Administrator count is within range.";
        }

        return areaFindings.Count == 0 ? "No findings." : Review(areaFindings.Count);
    }

    /// <summary>The header verdict: distinct people and workload identities across the standing-access rules, then one clause per skipped source.</summary>
    public static Verdict VerdictFor(string tenantName, IReadOnlyList<Finding> allFindings, IReadOnlyList<SkippedSource> skipped)
    {
        ArgumentNullException.ThrowIfNull(tenantName);
        ArgumentNullException.ThrowIfNull(allFindings);
        ArgumentNullException.ThrowIfNull(skipped);
        var standing = allFindings.Where(f => StandingRules.Contains(f.Id)).ToList();
        var people = Distinct(standing.Where(f => f.Principal.Type == PrincipalType.User));
        var workload = Distinct(standing.Where(f => f.Principal.Type == PrincipalType.ServicePrincipal));
        string head;
        if (people == 0 && workload == 0)
        {
            head = $"No standing privileged access was found in {tenantName}.";
        }
        else
        {
            var who = people > 0 && workload > 0
                ? $"{Plural(people, "person", "people")} and {Plural(workload, "workload identity", "workload identities")} hold"
                : people > 0 ? Plural(people, "person holds", "people hold") : Plural(workload, "workload identity holds", "workload identities hold");
            head = $"{who} standing privileged access in {tenantName}.";
        }

        var clauses = skipped.Select(s => s.Source).Distinct(StringComparer.OrdinalIgnoreCase).Select(SkippedClause).ToList();
        if (clauses.Count == 0)
        {
            return new Verdict(head, null);
        }

        var tail = string.Join("; ", clauses);
        return new Verdict(head, char.ToUpperInvariant(tail[0]) + tail[1..] + ".");
    }

    public static string SkippedClause(string source) => source.ToLowerInvariant() switch
    {
        "azure" => "Azure was not scanned",
        "azure-management-groups" => "Azure management groups were not scanned, so the Azure section under-counts",
        "pim-for-groups" => "PIM for Groups was not scanned",
        "groups" => "some groups could not be read, so the Entra and Guests sections under-count",
        "principals" => "some principals could not be resolved to names",
        _ => $"the {source} source was skipped",
    };

    private static bool Is(Finding f, string code) => string.Equals(f.Id, code, StringComparison.OrdinalIgnoreCase);

    private static int Distinct(IEnumerable<Finding> findings) => findings.Select(f => f.Principal.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static string Review(int count) => $"{Plural(count, "finding", "findings")} to review.";

    private static string FirstSentence(string text)
    {
        var i = text.IndexOf(". ", StringComparison.Ordinal);
        return i < 0 ? text : text[..(i + 1)];
    }
```

Note the `SkippedClause` capitalisation: "Azure was not scanned" already starts upper-case, and `VerdictFor` upper-cases the first character of the joined tail, which is a no-op there and fixes "some groups…" when it comes first.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~ReportAreasTests"`
Expected: all pass. If `Sentence_Entra…` fails on "more people", check the `Plural(through, "more person", "more people")` branch produces "2 more people".

- [ ] **Step 5: Commit**

```bash
git add audit/src/Elevate.Audit/Rendering/ReportAreas.cs audit/tests/Elevate.Audit.Tests/ReportAreasTests.cs
git commit -m "Audit: tile sentences and the header verdict"
```

---

### Task 3: `GroupRollup` — cards and the membership trie

**Files:**
- Create: `audit/src/Elevate.Audit/Rendering/GroupRollup.cs`
- Test: `audit/tests/Elevate.Audit.Tests/GroupRollupTests.cs`
- Modify: `docs/superpowers/specs/2026-09-14-audit-report-redesign-design.md` §5 (two amendments, step 6)

**Interfaces:**
- Produces: `class GroupNode { string Id; string Name; List<GroupNode> Children; List<Finding> People; int TotalPeople; int GroupCount }`; `record RollupCard(FindingPrincipal Group, FindingRole Role, FindingScope Scope, string Remedy, string PortalUrl, Finding? GroupFinding, IReadOnlyList<Finding> Members, GroupNode Tree) { int NestedGroups; int People }`; `GroupRollup.RollsUp(string ruleCode)`; `GroupRollup.Build(string ruleCode, IReadOnlyList<Finding> findings)` returning `(IReadOnlyList<RollupCard> Cards, IReadOnlyList<Finding> Rows)`; `GroupRollup.Tree(FindingPrincipal group, IReadOnlyList<Finding> members)`.

Design notes the executor must know:
- `ENTRA-GROUP-PERMANENT` and `AZURE-PERMANENT` emit one group-level finding (principal type Group, empty `Via`) and one finding per person with `Via[0]` = the assigned group. `GROUP-MEMBER-PERMANENT` emits per-person findings with empty `Via` whose `Scope` is the PIM-managed group and no group-level finding.
- `GROUP-MEMBER-PERMANENT` uses the group id as the role id for both the member and the owner role (`FindingRole(group.Id, "{name} (member|owner)", …)`), so its cards key on the role's display name, giving one card per group and access kind.
- `Via` for a person is the full chain starting with the assigned group itself (see `GroupExpansion.Expand`), so the trie is built from `Via.Skip(1)`.
- A finding of a rolled-up rule with a non-Group principal and empty `Via` (a direct Azure assignment to a user) stays a table row.

- [ ] **Step 1: Write the failing tests**

Create `audit/tests/Elevate.Audit.Tests/GroupRollupTests.cs`:

```csharp
using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class GroupRollupTests
{
    private static readonly FindingRole Ga = new("r-ga", "Global Administrator", null, true, RoleSystem.Entra);
    private static readonly FindingScope Dir = new("/", "Directory", ScopeKind.Directory);
    private static readonly GroupRef Top = new("g-top", "Tier 0 Admins");
    private static readonly GroupRef Plat = new("g-plat", "Platform Team");
    private static readonly GroupRef Ops = new("g-ops", "Ops Leads");
    private static readonly GroupRef Shared = new("g-shared", "Shared Pool");

    private static Finding Person(string id, IReadOnlyList<GroupRef> via, string code = "ENTRA-GROUP-PERMANENT", FindingRole? role = null, FindingScope? scope = null, bool guest = false) =>
        new(code, Severity.High, new FindingPrincipal(id, "Person " + id, id + "@contoso.com", PrincipalType.User, guest, true), role ?? Ga, scope ?? Dir, via,
            "remedy " + id, "https://portal.azure.com/#" + id, new FindingEvidence("a1", null, null, AssignmentType.Assigned, null));

    private static Finding Group(GroupRef g, string code = "ENTRA-GROUP-PERMANENT") =>
        new(code, Severity.High, new FindingPrincipal(g.Id, g.DisplayName, null, PrincipalType.Group, false, null), Ga, Dir, [],
            "group remedy", "https://portal.azure.com/#" + g.Id, new FindingEvidence("a1", null, null, AssignmentType.Assigned, null));

    /// <summary>One assigned group, three nested groups (Shared reached under both Platform and Ops), 120 people.</summary>
    private static List<Finding> Fixture()
    {
        var list = new List<Finding> { Group(Top) };
        for (var i = 0; i < 5; i++) list.Add(Person($"top{i}", [Top]));
        for (var i = 0; i < 40; i++) list.Add(Person($"plat{i}", [Top, Plat]));
        for (var i = 0; i < 30; i++) list.Add(Person($"ops{i}", [Top, Ops]));
        for (var i = 0; i < 25; i++) list.Add(Person($"sp{i}", [Top, Plat, Shared]));
        for (var i = 0; i < 20; i++) list.Add(Person($"so{i}", [Top, Ops, Shared]));
        return list;
    }

    [Theory]
    [InlineData("ENTRA-GROUP-PERMANENT", true)]
    [InlineData("AZURE-PERMANENT", true)]
    [InlineData("GROUP-MEMBER-PERMANENT", true)]
    [InlineData("ENTRA-USER-PERMANENT", false)]
    [InlineData("GUEST-PERMANENT", false)]
    public void RollsUp_OnlyTheGroupRules(string code, bool expected)
    {
        GroupRollup.RollsUp(code).Should().Be(expected);
    }

    [Fact]
    public void Build_OneCardPerAssignedGroup_WithEveryMember()
    {
        var (cards, rows) = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture());

        rows.Should().BeEmpty();
        var card = cards.Should().ContainSingle().Subject;
        card.Group.Id.Should().Be("g-top");
        card.GroupFinding.Should().NotBeNull();
        card.Remedy.Should().Be("group remedy");
        card.Members.Should().HaveCount(120);
        card.People.Should().Be(120);
        card.NestedGroups.Should().Be(4, "Platform, Ops, and Shared under each of them");
    }

    [Fact]
    public void Tree_IsATrieOfViaPaths_WithPeopleAtTheirLastGroup()
    {
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture()).Cards[0];
        var tree = card.Tree;

        tree.Id.Should().Be("g-top");
        tree.People.Should().HaveCount(5);
        tree.Children.Select(c => c.Name).Should().Equal("Platform Team", "Ops Leads");
        tree.Children[0].People.Should().HaveCount(40);
        tree.Children[0].Children.Should().ContainSingle().Which.Name.Should().Be("Shared Pool");
        tree.Children[0].Children[0].People.Should().HaveCount(25);
        tree.Children[1].Children[0].People.Should().HaveCount(20);
        tree.TotalPeople.Should().Be(120);
        tree.GroupCount.Should().Be(5);
    }

    [Fact]
    public void Build_SeparatesCardsByRoleAndScope_AndKeepsDirectFindingsAsRows()
    {
        var reader = new FindingRole("r-reader", "Reader", null, false, RoleSystem.Azure);
        var sub = new FindingScope("/subscriptions/a", "Prod", ScopeKind.Subscription);
        var f = new List<Finding>
        {
            Group(Top, "AZURE-PERMANENT") with { Role = reader, Scope = sub },
            Person("u1", [Top], "AZURE-PERMANENT", reader, sub),
            Person("u2", [], "AZURE-PERMANENT", reader, sub),
            Group(Top, "AZURE-PERMANENT") with { Scope = new FindingScope("/subscriptions/b", "Dev", ScopeKind.Subscription), Role = reader },
        };

        var (cards, rows) = GroupRollup.Build("AZURE-PERMANENT", f);

        cards.Should().HaveCount(2);
        cards[0].Members.Should().ContainSingle().Which.Principal.Id.Should().Be("u1");
        cards[1].Members.Should().BeEmpty();
        rows.Should().ContainSingle().Which.Principal.Id.Should().Be("u2");
    }

    [Fact]
    public void Build_SynthesisesACard_WhenTheGroupFindingIsMissing()
    {
        var (cards, _) = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Person("u1", [Top, Plat]), Person("u2", [Top])]);

        var card = cards.Should().ContainSingle().Subject;
        card.GroupFinding.Should().BeNull();
        card.Group.Id.Should().Be("g-top");
        card.Group.DisplayName.Should().Be("Tier 0 Admins");
        card.Group.Type.Should().Be(PrincipalType.Group);
        card.Remedy.Should().Be("remedy u1");
        card.Members.Should().HaveCount(2);
    }

    [Fact]
    public void Build_PimGroupMembers_RollUpByTheScopeGroup()
    {
        var g1 = new FindingScope("g-pim1", "Tier 0 Admins", ScopeKind.Group);
        var g2 = new FindingScope("g-pim2", "Helpdesk", ScopeKind.Group);
        var member1 = new FindingRole("g-pim1", "Tier 0 Admins (member)", null, true, RoleSystem.Group);
        var owner1 = new FindingRole("g-pim1", "Tier 0 Admins (owner)", null, true, RoleSystem.Group);
        var member2 = new FindingRole("g-pim2", "Helpdesk (member)", null, true, RoleSystem.Group);
        var f = new List<Finding>
        {
            Person("u1", [], "GROUP-MEMBER-PERMANENT", member1, g1),
            Person("u2", [], "GROUP-MEMBER-PERMANENT", owner1, g1),
            Person("u3", [], "GROUP-MEMBER-PERMANENT", member2, g2),
        };

        var (cards, rows) = GroupRollup.Build("GROUP-MEMBER-PERMANENT", f);

        rows.Should().BeEmpty();
        cards.Select(c => c.Group.Id).Should().Equal("g-pim1", "g-pim1", "g-pim2");
        cards[0].Role.DisplayName.Should().Be("Tier 0 Admins (member)");
        cards[0].NestedGroups.Should().Be(0);
        cards[0].Tree.People.Should().ContainSingle();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~GroupRollupTests"`
Expected: build error, `GroupRollup` missing.

- [ ] **Step 3: Write the implementation**

Create `audit/src/Elevate.Audit/Rendering/GroupRollup.cs`:

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

/// <summary>One group in a card's membership trie: the people reached at exactly this group, and the groups nested under it.</summary>
public sealed class GroupNode(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public List<GroupNode> Children { get; } = [];
    public List<Finding> People { get; } = [];
    public int TotalPeople => People.Count + Children.Sum(c => c.TotalPeople);
    public int GroupCount => 1 + Children.Sum(c => c.GroupCount);
}

/// <summary>
/// One group that grants <see cref="Role"/> on <see cref="Scope"/>: the group-level finding when the rule emitted one,
/// every per-person finding reached through the group, and the trie of nested groups.
/// </summary>
public sealed record RollupCard(
    FindingPrincipal Group,
    FindingRole Role,
    FindingScope Scope,
    string Remedy,
    string PortalUrl,
    Finding? GroupFinding,
    IReadOnlyList<Finding> Members,
    GroupNode Tree)
{
    public int NestedGroups => Tree.GroupCount - 1;
    public int People => Members.Count;
}

/// <summary>Folds per-person findings under the group that grants the access. A rendering concern only; findings and JSON are untouched.</summary>
public static class GroupRollup
{
    private const string PimGroupRule = "GROUP-MEMBER-PERMANENT";

    public static bool RollsUp(string ruleCode) =>
        ruleCode is not null && (Is(ruleCode, "ENTRA-GROUP-PERMANENT") || Is(ruleCode, "AZURE-PERMANENT") || Is(ruleCode, PimGroupRule));

    /// <summary>Cards in first-seen order, plus the findings that stay as plain rows (direct, non-group principals with no path).</summary>
    public static (IReadOnlyList<RollupCard> Cards, IReadOnlyList<Finding> Rows) Build(string ruleCode, IReadOnlyList<Finding> findings)
    {
        ArgumentNullException.ThrowIfNull(ruleCode);
        ArgumentNullException.ThrowIfNull(findings);
        var pimGroup = Is(ruleCode, PimGroupRule);
        var order = new List<string>();
        var groupFindings = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, List<Finding>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Finding>();
        foreach (var f in findings)
        {
            string key;
            var isGroupFinding = false;
            if (pimGroup)
            {
                // GROUP-MEMBER-PERMANENT gives the member and owner roles the same id (the group id); the display name tells them apart.
                key = Key(f.Scope.Id, f.Role.DisplayName, f.Scope.Id);
            }
            else if (f.Via.Count > 0)
            {
                key = Key(f.Via[0].Id, f.Role.Id, f.Scope.Id);
            }
            else if (f.Principal.Type == PrincipalType.Group)
            {
                key = Key(f.Principal.Id, f.Role.Id, f.Scope.Id);
                isGroupFinding = true;
            }
            else
            {
                rows.Add(f);
                continue;
            }

            if (!members.ContainsKey(key))
            {
                order.Add(key);
                members[key] = [];
            }

            if (isGroupFinding)
            {
                groupFindings.TryAdd(key, f);
            }
            else
            {
                members[key].Add(f);
            }
        }

        var cards = new List<RollupCard>(order.Count);
        foreach (var key in order)
        {
            var ms = members[key];
            var groupFinding = groupFindings.GetValueOrDefault(key);
            var first = groupFinding ?? ms[0];
            var group = groupFinding?.Principal ?? (pimGroup
                ? new FindingPrincipal(first.Scope.Id, first.Scope.DisplayName, null, PrincipalType.Group, false, null)
                : new FindingPrincipal(first.Via[0].Id, first.Via[0].DisplayName, null, PrincipalType.Group, false, null));
            cards.Add(new RollupCard(group, first.Role, first.Scope, first.Remedy, first.PortalUrl, groupFinding, ms, Tree(group, ms)));
        }

        return (cards, rows);
    }

    /// <summary>A trie of the members' <c>Via</c> paths below the assigned group; each person hangs off the last group in their path.</summary>
    public static GroupNode Tree(FindingPrincipal group, IReadOnlyList<Finding> members)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        var root = new GroupNode(group.Id, group.DisplayName);
        foreach (var m in members)
        {
            var node = root;
            foreach (var g in m.Via.Skip(1))
            {
                var child = node.Children.FirstOrDefault(c => string.Equals(c.Id, g.Id, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                {
                    child = new GroupNode(g.Id, g.DisplayName);
                    node.Children.Add(child);
                }

                node = child;
            }

            node.People.Add(m);
        }

        return root;
    }

    private static string Key(string group, string role, string scope) => $"{group}|{role}|{scope}";

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~GroupRollupTests"`
Expected: all pass.

- [ ] **Step 5: Amend the spec for the two decisions this task made**

In `docs/superpowers/specs/2026-09-14-audit-report-redesign-design.md` §5 "Which findings roll up", replace the first sentence:

```
**Which findings roll up.** `ENTRA-GROUP-PERMANENT` and
`GROUP-MEMBER-PERMANENT`. For `ENTRA-GROUP-PERMANENT` the key is (group
```

with:

```
**Which findings roll up.** `ENTRA-GROUP-PERMANENT`, `AZURE-PERMANENT` and
`GROUP-MEMBER-PERMANENT` (`AZURE-PERMANENT` emits the same group-plus-members
shape when a group holds an Azure role; its direct user and service-principal
findings stay as rows). For `ENTRA-GROUP-PERMANENT` and `AZURE-PERMANENT` the key is (group
```

and in the "Membership outline" bullet replace "node identity is the group id, order is first-seen." with "node identity is the group id within its parent, so a group reachable through two parents appears under each parent that reaches people through it; order is first-seen."

- [ ] **Step 6: Commit**

```bash
git add audit/src/Elevate.Audit/Rendering/GroupRollup.cs audit/tests/Elevate.Audit.Tests/GroupRollupTests.cs docs/superpowers/specs/2026-09-14-audit-report-redesign-design.md
git commit -m "Audit: group roll-up cards and the membership trie"
```

---

### Task 4: `GroupRollup` — outline HTML and the SVG nesting diagram

**Files:**
- Modify: `audit/src/Elevate.Audit/Rendering/GroupRollup.cs`
- Test: `audit/tests/Elevate.Audit.Tests/GroupRollupTests.cs`

**Interfaces:**
- Consumes: Task 3.
- Produces: `GroupRollup.Outline(RollupCard card)` → `string` (a `<ul class="tree">`); `GroupRollup.Diagram(RollupCard card)` → `string?` (an inline `<svg>` or null); `GroupRollup.MaxDiagramGroups = 12`; `GroupRollup.MaxInlinePeople = 10`.

- [ ] **Step 1: Write the failing tests**

Append to `GroupRollupTests`:

```csharp
    [Fact]
    public void Outline_ListsGroupsWithCounts_InlinesSmallGroups_AndSummarisesLargeOnes()
    {
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture()).Cards[0];

        var html = GroupRollup.Outline(card);

        html.Should().StartWith("<ul class=\"tree\">").And.EndWith("</ul>");
        html.Should().Contain("Global Administrator");
        html.Should().Contain("Tier 0 Admins").And.Contain("5 people direct · 2 nested groups");
        html.Should().Contain("Person top0"); // 5 direct people are inlined
        html.Should().NotContain("Person plat0"); // 40 are not
        html.Should().Contain("40 people, listed below");
        html.Should().Contain("Shared Pool");
        System.Text.RegularExpressions.Regex.Matches(html, "<ul").Count.Should().Be(System.Text.RegularExpressions.Regex.Matches(html, "</ul>").Count);
        System.Text.RegularExpressions.Regex.IsMatch(html, "<ul[^>]*>\\s*<ul").Should().BeFalse("nested lists live inside an <li>");
    }

    [Fact]
    public void Outline_MarksGuests_AndEscapes()
    {
        var evil = new GroupRef("g-x", "<Evil>");
        var (cards, _) = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Group(evil), Person("u1", [evil], guest: true)]);

        var html = GroupRollup.Outline(cards[0]);

        html.Should().Contain("&lt;Evil&gt;").And.NotContain("<Evil>");
        html.Should().Contain("<span class=\"pill\">guest</span>");
    }

    [Fact]
    public void Diagram_DrawsRoleAndEveryGroup_WithPeopleCounts()
    {
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", Fixture()).Cards[0];

        var svg = GroupRollup.Diagram(card);

        svg.Should().NotBeNull();
        svg.Should().StartWith("<svg").And.EndWith("</svg>");
        svg.Should().Contain("Global Administrator").And.Contain("Tier 0 Admins").And.Contain("Platform Team").And.Contain("Ops Leads");
        System.Text.RegularExpressions.Regex.Matches(svg!, "<rect").Count.Should().Be(6, "role + 5 group nodes");
        System.Text.RegularExpressions.Regex.Matches(svg!, "<path").Count.Should().Be(5, "one edge per non-root node plus role→group");
        svg.Should().Contain("5 people").And.Contain("40 people").And.Contain("25 people");
        svg.Should().NotContain("Person ");
        svg.Should().Contain("viewBox=\"0 0 ");
    }

    [Fact]
    public void Diagram_IsOmitted_WithoutNesting_OrAboveTheGroupCap()
    {
        var flat = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Group(Top), Person("u1", [Top])]).Cards[0];
        GroupRollup.Diagram(flat).Should().BeNull();

        var big = new List<Finding> { Group(Top) };
        for (var i = 0; i < GroupRollup.MaxDiagramGroups; i++)
        {
            big.Add(Person($"u{i}", [Top, new GroupRef($"g{i}", $"Group {i}")]));
        }

        GroupRollup.Diagram(GroupRollup.Build("ENTRA-GROUP-PERMANENT", big).Cards[0]).Should().BeNull("13 groups exceed the cap of 12");
        big.RemoveAt(big.Count - 1);
        GroupRollup.Diagram(GroupRollup.Build("ENTRA-GROUP-PERMANENT", big).Cards[0]).Should().NotBeNull("12 groups are within the cap");
    }

    [Fact]
    public void Diagram_EllipsisesLongNames_AndKeepsTheFullNameInATitle()
    {
        var longName = new GroupRef("g-long", "A Very Long Group Name Indeed");
        var card = GroupRollup.Build("ENTRA-GROUP-PERMANENT", [Group(Top), Person("u1", [Top, longName])]).Cards[0];

        var svg = GroupRollup.Diagram(card)!;

        svg.Should().Contain("A Very Long Group…").And.Contain("<title>A Very Long Group Name Indeed</title>");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~GroupRollupTests"`
Expected: build error, `Outline`, `Diagram`, `MaxDiagramGroups` missing.

- [ ] **Step 3: Write the implementation**

Add `using System.Globalization; using System.Net; using System.Text;` at the top of `GroupRollup.cs` and add to the class:

```csharp
    public const int MaxDiagramGroups = 12;
    public const int MaxInlinePeople = 10;

    private const int NodeW = 140;
    private const int NodeH = 46;
    private const int TierX = 180;
    private const int RowY = 66;
    private const int Pad = 10;

    /// <summary>The membership outline: role, assigned group, nested groups with counts; people inlined for groups with at most <see cref="MaxInlinePeople"/> direct people.</summary>
    public static string Outline(RollupCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var b = new StringBuilder();
        b.Append("<ul class=\"tree\"><li class=\"role\"><span class=\"name\">").Append(E(card.Role.DisplayName)).Append("</span><span class=\"cnt\">").Append(E(card.Scope.DisplayName)).Append("</span><ul>");
        Node(b, card.Tree);
        b.Append("</ul></li></ul>");
        return b.ToString();
    }

    private static void Node(StringBuilder b, GroupNode n)
    {
        b.Append("<li class=\"grp\"><span class=\"name\">").Append(E(n.Name)).Append("</span><span class=\"cnt\">")
         .Append(ReportAreas.Plural(n.People.Count, "person", "people")).Append(" direct");
        if (n.Children.Count > 0)
        {
            b.Append(" · ").Append(ReportAreas.Plural(n.Children.Count, "nested group", "nested groups"));
        }

        b.Append("</span>");
        if (n.People.Count > 0 || n.Children.Count > 0)
        {
            b.Append("<ul>");
            if (n.People.Count <= MaxInlinePeople)
            {
                foreach (var p in n.People)
                {
                    b.Append("<li class=\"person\"><span class=\"name\">").Append(E(p.Principal.DisplayName)).Append("</span>")
                     .Append(p.Principal.IsGuest ? "<span class=\"pill\">guest</span>" : string.Empty).Append("</li>");
                }
            }
            else
            {
                b.Append("<li class=\"person muted\">").Append(n.People.Count.ToString(CultureInfo.InvariantCulture)).Append(" people, listed below</li>");
            }

            foreach (var c in n.Children)
            {
                Node(b, c);
            }

            b.Append("</ul>");
        }

        b.Append("</li>");
    }

    /// <summary>
    /// A left-to-right tiered diagram of the role and the groups (never people). Null when the group grants directly
    /// or the trie has more than <see cref="MaxDiagramGroups"/> groups.
    /// </summary>
    public static string? Diagram(RollupCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.NestedGroups == 0 || card.Tree.GroupCount > MaxDiagramGroups)
        {
            return null;
        }

        var placed = new List<(GroupNode Node, int Depth, double Row, GroupNode? Parent)>();
        var nextRow = 0;
        double Place(GroupNode n, int depth, GroupNode? parent)
        {
            double row;
            if (n.Children.Count == 0)
            {
                row = nextRow++;
            }
            else
            {
                var rows = n.Children.Select(c => Place(c, depth + 1, n)).ToList();
                row = (rows[0] + rows[^1]) / 2;
            }

            placed.Add((n, depth, row, parent));
            return row;
        }

        var rootRow = Place(card.Tree, 1, null);
        var maxDepth = placed.Max(p => p.Depth);
        var width = Pad * 2 + maxDepth * TierX + NodeW;
        var height = Pad * 2 + (nextRow - 1) * RowY + NodeH;
        var b = new StringBuilder();
        b.Append("<svg class=\"nesting\" viewBox=\"0 0 ").Append(width).Append(' ').Append(height).Append("\" width=\"").Append(width).Append("\" height=\"").Append(height)
         .Append("\" role=\"img\" aria-label=\"How the groups nest\" font-family=\"-apple-system,BlinkMacSystemFont,Segoe UI,Helvetica,Arial,sans-serif\">");

        // Edges first so nodes paint over them.
        var pos = placed.ToDictionary(p => p.Node, p => (X: Pad + p.Depth * TierX, Y: Pad + (int)Math.Round(p.Row * RowY)));
        var roleX = Pad;
        var roleY = Pad + (int)Math.Round(rootRow * RowY);
        Edge(b, roleX + NodeW, roleY + NodeH / 2, pos[card.Tree].X, pos[card.Tree].Y + NodeH / 2);
        foreach (var p in placed.Where(p => p.Parent is not null))
        {
            var from = pos[p.Parent!];
            var to = pos[p.Node];
            Edge(b, from.X + NodeW, from.Y + NodeH / 2, to.X, to.Y + NodeH / 2);
        }

        Rect(b, roleX, roleY, card.Role.DisplayName, E(card.Role.System == RoleSystem.Azure ? "Azure role" : "Entra role"), "role");
        foreach (var p in placed)
        {
            var (x, y) = pos[p.Node];
            Rect(b, x, y, p.Node.Name, ReportAreas.Plural(p.Node.People.Count, "person", "people"), "group");
        }

        b.Append("</svg>");
        return b.ToString();
    }

    private static void Edge(StringBuilder b, int x1, int y1, int x2, int y2)
    {
        var mx = (x1 + x2) / 2;
        b.Append("<path d=\"M").Append(x1).Append(' ').Append(y1).Append(" C ").Append(mx).Append(' ').Append(y1).Append(", ").Append(mx).Append(' ').Append(y2).Append(", ").Append(x2).Append(' ').Append(y2)
         .Append("\" fill=\"none\" stroke=\"#dce3ec\" stroke-width=\"1.5\"/>");
    }

    private static void Rect(StringBuilder b, int x, int y, string label, string sub, string kind)
    {
        var (fill, stroke) = kind == "role" ? ("#fee4e2", "#b42318") : ("#fff", "#075bd8");
        var shown = label.Length > 18 ? label[..17] + "…" : label;
        b.Append("<g class=\"").Append(kind).Append("\"><title>").Append(E(label)).Append("</title>")
         .Append("<rect x=\"").Append(x).Append("\" y=\"").Append(y).Append("\" width=\"").Append(NodeW).Append("\" height=\"").Append(NodeH).Append("\" rx=\"10\" fill=\"").Append(fill).Append("\" stroke=\"").Append(stroke).Append("\" stroke-width=\"1.5\"/>")
         .Append("<text x=\"").Append(x + 12).Append("\" y=\"").Append(y + 19).Append("\" font-size=\"13\" font-weight=\"600\" fill=\"#101d32\">").Append(E(shown)).Append("</text>")
         .Append("<text x=\"").Append(x + 12).Append("\" y=\"").Append(y + 36).Append("\" font-size=\"11.5\" fill=\"#526176\">").Append(sub).Append("</text></g>");
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
```

`StringBuilder.Append(int)` uses the current culture for negative signs only; all numbers here are non-negative, so no culture issue. The diagram sub-label passed to `Rect` is already text (the role kind label is encoded at the call site; `Plural` output has no special characters).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~GroupRollupTests"`
Expected: all pass. If the `<path>` count is off, remember there is one edge from the role to the root plus one per non-root node: 1 + 4 = 5 for the fixture.

- [ ] **Step 5: Commit**

```bash
git add audit/src/Elevate.Audit/Rendering/GroupRollup.cs audit/tests/Elevate.Audit.Tests/GroupRollupTests.cs
git commit -m "Audit: membership outline and the group nesting diagram"
```

---

### Task 5: `AuditReport.AllFindings` and the embedded script resource

**Files:**
- Modify: `audit/src/Elevate.Audit/Rendering/AuditReport.cs`
- Create: `audit/src/Elevate.Audit/Rendering/report.js`
- Modify: `audit/src/Elevate.Audit/Elevate.Audit.csproj:45-46`
- Test: `audit/tests/Elevate.Audit.Tests/HtmlRendererTests.cs`, `audit/tests/Elevate.Audit.Tests/JsonAndSnapshotTests.cs`

**Interfaces:**
- Produces: `AuditReport.AllFindings` (`IReadOnlyList<Finding>`, `[JsonIgnore]`, every finding regardless of `--min-severity`); `HtmlRenderer.Script` (the embedded `report.js` text); resource name `Elevate.Audit.Resources.report.js`.

- [ ] **Step 1: Write the failing tests**

Add to `JsonAndSnapshotTests.cs` (inside the existing class):

```csharp
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
```

Add to `HtmlRendererTests.cs`:

```csharp
    [Fact]
    public void Script_IsEmbedded_AndSelfContained()
    {
        HtmlRenderer.Script.Should().Contain("'use strict'").And.NotContain("fetch(").And.NotContain("import ");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~AllFindings_Carries|FullyQualifiedName~Script_IsEmbedded"`
Expected: build errors for `AllFindings` and `Script`.

- [ ] **Step 3: Add `AllFindings`**

In `AuditReport.cs`, add `using System.Text.Json.Serialization;` and change the record body and `From`:

```csharp
public sealed record AuditReport(
    ReportTool Tool,
    TenantInfo Tenant,
    string Account,
    DateTimeOffset ScannedAt,
    ReportOptions Options,
    IReadOnlyList<string> ScopesRequested,
    IReadOnlyList<SkippedSource> Skipped,
    ReportSummary Summary,
    int Hidden,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>Every finding, before <c>--min-severity</c>; the HTML summary reads this. Never serialised.</summary>
    [JsonIgnore]
    public IReadOnlyList<Finding> AllFindings { get; init; } = Findings;
```

and in `From(...)`, wrap the constructed record so the property is set:

```csharp
        return new AuditReport(
            /* existing arguments unchanged */
            visible)
        { AllFindings = findings };
```

- [ ] **Step 4: Create `report.js`**

Create `audit/src/Elevate.Audit/Rendering/report.js`:

```js
(function () {
  'use strict';
  var d = document;
  var ROW_CAP = 50;
  var START_CAP = 10;
  function q(sel, root) { return Array.prototype.slice.call((root || d).querySelectorAll(sel)); }

  var main = d.querySelector('main');
  if (!main) { return; }

  // Toolbar: search, severity chips, expand/collapse. Only exists when scripts run.
  var bar = d.createElement('div');
  bar.className = 'toolbar';
  bar.innerHTML =
    '<label class="search"><span class="visually-hidden">Search findings</span><input type="search" placeholder="Search people, groups, roles, scopes"></label>' +
    ['high', 'medium', 'low', 'info'].map(function (s) {
      var on = s === 'high' || s === 'medium';
      return '<button type="button" class="chip' + (on ? ' on' : '') + '" data-severity="' + s + '" aria-pressed="' + on + '"><span class="dot ' + s + '"></span>' + s + '</button>';
    }).join('') +
    '<span class="sep"></span>' +
    '<button type="button" class="chip" data-open="1">Expand all</button>' +
    '<button type="button" class="chip" data-open="0">Collapse all</button>';
  main.insertBefore(bar, main.firstChild);
  var input = bar.querySelector('input');
  var off = { low: true, info: true };

  // "No findings match." note per area, inserted here so the no-script page carries no hidden markup.
  q('details.area').forEach(function (area) {
    var p = d.createElement('p');
    p.className = 'nomatch muted';
    p.textContent = 'No findings match.';
    p.hidden = true;
    area.appendChild(p);
  });

  // Caps: hide rows past the limit and offer "Show all".
  function cap(container, rows, limit, noun) {
    if (rows.length <= limit) { return; }
    rows.slice(limit).forEach(function (r) { r.hidden = true; r.setAttribute('data-capped', '1'); });
    var more = d.createElement('div');
    more.className = 'more';
    more.innerHTML = '<span>Showing ' + limit + ' of ' + rows.length + (noun ? ' ' + noun : '') + '</span><button type="button" class="btn">Show all</button>';
    more.querySelector('button').addEventListener('click', function () {
      rows.forEach(function (r) { r.hidden = false; r.removeAttribute('data-capped'); });
      more.parentNode.removeChild(more);
      apply();
    });
    container.appendChild(more);
  }
  q('.table-scroll').forEach(function (scroll) { cap(scroll, q('tbody > tr', scroll), ROW_CAP); });
  var start = d.querySelector('ol.start');
  if (start) { cap(start.parentNode, q(':scope > li', start), START_CAP, 'actions'); }

  function apply() {
    var term = input.value.trim().toLowerCase();
    q('[data-search]').forEach(function (el) {
      var sev = el.getAttribute('data-severity');
      var miss = term && el.getAttribute('data-search').indexOf(term) < 0;
      var capped = !term && el.hasAttribute('data-capped');
      el.hidden = !!((sev && off[sev]) || miss || capped);
    });
    q('details.rule').forEach(function (rule) {
      var sev = rule.getAttribute('data-severity');
      var any = q('[data-search]', rule).some(function (el) { return !el.hidden; });
      rule.hidden = !!(off[sev] || (term && !any));
      if (term && any) { rule.open = true; }
    });
    q('details.area').forEach(function (area) {
      var rules = q('details.rule', area);
      var any = rules.some(function (r) { return !r.hidden; });
      area.querySelector('.nomatch').hidden = any || rules.length === 0;
      if (term && any) { area.open = true; }
    });
  }

  input.addEventListener('input', apply);
  q('.chip[data-severity]', bar).forEach(function (chip) {
    chip.addEventListener('click', function () {
      var s = chip.getAttribute('data-severity');
      off[s] = !off[s];
      chip.classList.toggle('on', !off[s]);
      chip.setAttribute('aria-pressed', String(!off[s]));
      apply();
    });
  });
  q('.chip[data-open]', bar).forEach(function (chip) {
    chip.addEventListener('click', function () {
      var open = chip.getAttribute('data-open') === '1';
      q('details').forEach(function (x) { x.open = open; });
    });
  });

  function openHash() {
    var id = location.hash.slice(1);
    if (!id) { return; }
    var el = d.getElementById(id);
    var det = el && el.closest('details');
    while (det) { det.open = true; det = det.parentElement && det.parentElement.closest('details'); }
  }
  addEventListener('hashchange', openHash);
  openHash();
  addEventListener('beforeprint', function () {
    q('details').forEach(function (x) { x.open = true; });
    q('[data-capped]').forEach(function (r) { r.hidden = false; });
  });

  apply();
})();
```

- [ ] **Step 5: Embed it and expose it**

In `Elevate.Audit.csproj`, after the `report.css` `EmbeddedResource` line add:

```xml
    <EmbeddedResource Include="Rendering\report.js" LogicalName="Elevate.Audit.Resources.report.js" />
```

In `HtmlRenderer.cs`, next to `StylesheetResource`/`StylesheetText`, add:

```csharp
    internal const string ScriptResource = "Elevate.Audit.Resources.report.js";

    private static readonly Lazy<string> ScriptText = new(() => ReadResource(ScriptResource, "report.js"));

    public static string Script => ScriptText.Value;

    private static string ReadResource(string name, string file)
    {
        using var stream = typeof(HtmlRenderer).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{file} missing from the bundle");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
```

and change `StylesheetText` to `new(() => ReadResource(StylesheetResource, "report.css"))`.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass, including the JSON golden (the new property is ignored).

- [ ] **Step 7: Commit**

```bash
git add audit/src/Elevate.Audit/Rendering/AuditReport.cs audit/src/Elevate.Audit/Rendering/report.js audit/src/Elevate.Audit/Rendering/HtmlRenderer.cs audit/src/Elevate.Audit/Elevate.Audit.csproj audit/tests/Elevate.Audit.Tests/JsonAndSnapshotTests.cs audit/tests/Elevate.Audit.Tests/HtmlRendererTests.cs
git commit -m "Audit: expose unfiltered findings to the renderer; embed the report script"
```

---

### Task 6: Stylesheet for the new components

**Files:**
- Modify: `audit/src/Elevate.Audit/Rendering/report.css`
- Test: existing `Stylesheet_TokensMatchTheProductPage`

- [ ] **Step 1: Replace everything below the `:root` block**

Keep the `:root { … }` block byte-for-byte. Replace the rest of `report.css` with:

```css
* { box-sizing: border-box; }
body { margin: 0; background: #fff; color: var(--ink); font-family: var(--font); -webkit-font-smoothing: antialiased; line-height: 1.5; }
a { color: var(--blue); text-decoration: none; }
a:hover { text-decoration: underline; text-underline-offset: 4px; }
.wrap { width: min(1120px, calc(100% - 48px)); margin-inline: auto; }
.visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
.report-header { background: var(--mist); border-bottom: 1px solid var(--line); padding-block: 40px 32px; }
.eyebrow { margin: 0 0 8px; color: var(--muted); font-size: 14px; letter-spacing: 0.04em; text-transform: uppercase; }
h1 { margin: 0 0 12px; font-size: clamp(32px, 4vw, 48px); letter-spacing: -0.02em; line-height: 1.1; text-wrap: balance; }
h2 { margin: 0; font-size: 24px; letter-spacing: -0.01em; }
h3 { margin: 0; font-size: 17px; }
.meta { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px 24px; margin: 0; padding: 0; list-style: none; color: var(--muted); font-size: 14px; }
.meta strong { display: block; color: var(--ink); font-weight: 600; }
.verdict { margin: 24px 0 0; font-size: clamp(18px, 2vw, 22px); line-height: 1.35; letter-spacing: -0.01em; max-width: 900px; text-wrap: pretty; }
.tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; margin-top: 24px; }
.tile { display: flex; flex-direction: column; gap: 8px; background: #fff; border: 1px solid var(--line); border-radius: 20px; padding: 20px; color: inherit; }
.tile:hover { text-decoration: none; border-color: var(--blue); }
.tile .head { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.tile .name { font-weight: 600; font-size: 15px; }
.tile .counts { display: flex; gap: 12px; align-items: baseline; flex-wrap: wrap; min-height: 32px; }
.tile .n { font-size: 32px; font-weight: 700; letter-spacing: -0.02em; line-height: 1; }
.tile .n.high { color: #b42318; } .tile .n.medium { color: #b54708; } .tile .n.low { color: var(--blue); } .tile .n.info { color: var(--muted); } .tile .n.skipped { color: #b54708; }
.tile .unit { color: var(--muted); font-size: 13px; }
.tile .line { color: var(--muted); font-size: 13px; margin: 0; }
.pill { display: inline-block; border-radius: 999px; padding: 2px 10px; font-size: 12px; font-weight: 600; letter-spacing: 0.02em; text-transform: uppercase; background: var(--mist); color: var(--muted); white-space: nowrap; }
.pill.high, .pill.critical { background: #fee4e2; color: #b42318; }
.pill.medium, .pill.attention, .pill.skipped { background: #fef0c7; color: #b54708; }
.pill.low { background: #e0ecff; color: var(--blue); }
.pill.clean, .pill.read { background: #dcfae6; color: #067647; }
.muted { color: var(--muted); }
.notice { background: var(--mist); border: 1px solid var(--line); border-radius: 16px; padding: 16px 20px; margin-top: 24px; }
.toolbar { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; background: #fff; border: 1px solid var(--line); border-radius: 16px; padding: 10px 12px; margin-top: 24px; position: sticky; top: 12px; z-index: 2; box-shadow: 0 6px 24px rgba(16, 29, 50, 0.06); }
.search { display: flex; align-items: center; flex: 1 1 240px; }
.search input { width: 100%; border: 1px solid var(--line); border-radius: 10px; padding: 8px 12px; font: inherit; font-size: 14px; background: var(--mist); color: var(--ink); }
.chip { display: inline-flex; align-items: center; gap: 6px; border: 1px solid var(--line); border-radius: 999px; padding: 6px 12px; font: inherit; font-size: 13px; font-weight: 600; color: var(--ink); background: #fff; cursor: pointer; text-transform: capitalize; }
.chip.on { background: var(--ink); color: #fff; border-color: var(--ink); }
.chip .dot { width: 8px; height: 8px; border-radius: 50%; }
.dot.high { background: #b42318; } .dot.medium { background: #b54708; } .dot.low { background: var(--blue); } .dot.info { background: var(--muted); }
.sep { width: 1px; height: 24px; background: var(--line); }
.section-title { display: flex; align-items: center; gap: 12px; margin-top: 40px; }
.section-title p { margin: 4px 0 0; }
.start { margin: 16px 0 0; padding: 0; list-style: none; display: grid; gap: 12px; }
.start li { border: 1px solid var(--line); border-radius: 16px; padding: 16px 20px; display: grid; gap: 4px; }
.start .who { font-weight: 600; }
.start .what { color: var(--muted); font-size: 14px; }
details.area { border-top: 1px solid var(--line); padding-top: 24px; margin-top: 32px; }
details.area > summary { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; cursor: pointer; list-style: none; }
details.area > summary::-webkit-details-marker { display: none; }
details.area > summary h2 { flex: 1 1 auto; }
.area-desc { margin: 8px 0 0 32px; color: var(--muted); font-size: 14px; }
.caret { width: 20px; height: 20px; flex: none; transition: transform 0.15s; }
details[open] > summary .caret { transform: rotate(90deg); }
details.rule { border: 1px solid var(--line); border-radius: 20px; padding: 16px 24px; margin-top: 16px; }
details.rule > summary { display: flex; align-items: baseline; flex-wrap: wrap; gap: 12px; cursor: pointer; list-style: none; }
details.rule > summary::-webkit-details-marker { display: none; }
details.rule > summary .desc { color: var(--muted); font-size: 14px; flex: 1 1 auto; }
details.rule > summary .caret { align-self: center; }
.count { color: var(--muted); font-size: 14px; white-space: nowrap; }
.group { border: 1px solid var(--line); border-radius: 16px; padding: 16px 20px; margin-top: 12px; }
.group .title { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; }
.group .who { font-weight: 600; }
.group .what { color: var(--muted); font-size: 14px; margin: 4px 0 0; }
.group .body { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 24px; margin-top: 16px; align-items: start; }
.group .body > div { display: flex; flex-direction: column; gap: 6px; }
.label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; font-weight: 600; }
.nesting { max-width: 100%; height: auto; }
.tree, .tree ul { list-style: none; margin: 0; padding: 0; font-size: 14px; }
.tree ul { padding-left: 22px; margin-left: 9px; border-left: 1px solid var(--line); }
.tree li > .name { font-weight: 600; }
.tree li.person > .name { font-weight: 400; }
.tree .cnt { color: var(--muted); font-size: 13px; margin-left: 8px; }
.tree li { padding: 4px 0; }
.tree .pill { margin-left: 8px; }
details.members { margin-top: 16px; border-top: 1px solid var(--line); padding-top: 8px; }
details.members > summary { cursor: pointer; font-weight: 600; font-size: 14px; display: flex; align-items: center; gap: 8px; list-style: none; padding: 6px 0; }
details.members > summary::-webkit-details-marker { display: none; }
.table-scroll { overflow-x: auto; margin-top: 8px; }
table { width: 100%; border-collapse: collapse; font-size: 14px; }
th, td { text-align: left; vertical-align: top; padding: 10px 12px; border-top: 1px solid var(--line); }
th { color: var(--muted); font-weight: 600; border-top: 0; }
td .muted { color: var(--muted); }
details { margin-top: 6px; }
summary { cursor: pointer; }
td details > summary { color: var(--blue); }
.path { font-size: 13px; color: var(--muted); }
.more { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 12px; border-top: 1px solid var(--line); color: var(--muted); font-size: 13px; }
.btn { display: inline-flex; align-items: center; border: 1px solid var(--line); border-radius: 999px; padding: 6px 14px; font: inherit; font-size: 13px; font-weight: 600; color: var(--ink); background: #fff; cursor: pointer; }
.nomatch { margin: 12px 0 0 32px; font-size: 14px; }
.appendix { margin-top: 48px; padding-top: 24px; border-top: 1px solid var(--line); color: var(--muted); font-size: 14px; }
.appendix > summary { display: flex; align-items: baseline; gap: 12px; list-style: none; }
.appendix > summary::-webkit-details-marker { display: none; }
.appendix > summary h2 { font-size: 20px; color: var(--ink); }
.appendix code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13px; color: var(--ink); }
footer { margin-block: 48px 64px; color: var(--muted); font-size: 13px; }
@media (max-width: 760px) {
  .wrap { width: calc(100% - 32px); }
  details.rule { padding: 16px; }
  .group .body { grid-template-columns: repeat(1, minmax(0, 1fr)); }
  .group .body .diagram { display: none; }
  .area-desc, .nomatch { margin-left: 0; }
}
@media print {
  .toolbar, .more { display: none; }
  details.rule, .group, .start li, .tile { break-inside: avoid; }
  a { color: inherit; }
  summary .caret { display: none; }
  details > summary { list-style: none; }
  [hidden] { display: none; }
}
```

- [ ] **Step 2: Run the token test**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~Stylesheet_TokensMatchTheProductPage"`
Expected: PASS (the `:root` block was not touched).

- [ ] **Step 3: Commit**

```bash
git add audit/src/Elevate.Audit/Rendering/report.css
git commit -m "Audit: report stylesheet for tiles, areas, roll-up cards and the toolbar"
```

---

### Task 7: Rewrite `HtmlRenderer`

**Files:**
- Modify: `audit/src/Elevate.Audit/Rendering/HtmlRenderer.cs` (the `Render` method and helpers; keep `Stylesheet`, `Script`, `ReadResource`, `E`)
- Test: `audit/tests/Elevate.Audit.Tests/HtmlRendererTests.cs`

**Interfaces:**
- Consumes: `ReportAreas.*` (Tasks 1–2), `GroupRollup.*` (Tasks 3–4), `AuditReport.AllFindings`, `HtmlRenderer.Script` (Task 5).
- Produces: the page structure of spec §3–§5. Markup contracts the script relies on: `main` element; `ol.start > li[data-search][data-severity]`; `details.area` with `summary[id=<area id>]`; `details.rule[id=<code>-<severity>][data-severity]`; `.table-scroll > table > tbody > tr[data-search]`; `.group[data-search]`; `details.members`.

- [ ] **Step 1: Update and extend the renderer tests**

In `HtmlRendererTests.cs`:

Change `Render_IsSelfContained_HasOneH1_AndOnlyHttpsLinks` to also assert the script is inline and the no-script markup is clean; replace its last two lines with:

```csharp
        html.Should().Contain("Tier 0 Admins").And.NotContain("<script src");
        html.Should().Contain("<details");
        html.Should().Contain("<script>\n" + HtmlRenderer.Script.TrimEnd());
        Regex.IsMatch(html, "<[a-z]+[^>]*\\shidden[\\s>]").Should().BeFalse("the no-script page must not hide anything");
```

Add these tests to the class:

```csharp
    [Fact]
    public void Render_Header_HasVerdictAndOneTilePerArea_LinkingToSections()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<p class=\"verdict\">");
        html.Should().Contain("hold standing privileged access in Contoso.");
        html.Should().Contain("Azure management groups were not scanned, so the Azure section under-counts.");
        var hrefs = Regex.Matches(html, "<a class=\"tile[^\"]*\" href=\"#([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        hrefs.Should().Equal("entra", "pim-groups", "azure", "guests", "workload", "hygiene", "coverage");
        foreach (var id in hrefs)
        {
            html.Should().Contain($"id=\"{id}\"", $"tile #{id} must resolve");
        }

        html.Should().NotContain("<div class=\"cards\">", "the severity cards are replaced by tiles");
    }

    [Fact]
    public void Render_Tiles_ShowStateAndUnderCounts()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<span class=\"pill critical\">Critical</span>");
        Regex.Matches(html, "<span class=\"pill skipped\">under-counts</span>").Count.Should().BeGreaterThan(0, "management groups were skipped");
        html.Should().Contain("<span class=\"pill skipped\">1 skipped</span>");
    }

    [Fact]
    public void Render_TilesUseUnfilteredFindings_WhenMinSeverityHidesSome()
    {
        var snapshot = SampleSnapshot.Build();
        var options = new AuditOptions(MinSeverity: Severity.High);
        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, findings, RuleRunner.Visible(findings, options), options, "sample", ClientIds.GraphReadScopeNames);

        var html = HtmlRenderer.Render(report);

        html.Should().Contain("eligibilities never expire", "hygiene findings are Low and hidden from the body, but the tile still counts them");
        html.Should().NotContain("id=\"eligible-no-end-low\"");
    }

    [Fact]
    public void Render_Areas_AreDetailsWithSummaryIds_HighAreasOpen()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<details class=\"area\" open><summary id=\"entra\">");
        html.Should().Contain("<details class=\"area\"><summary id=\"hygiene\">", "no High findings in hygiene");
        html.Should().Contain("<details class=\"rule\" id=\"azure-permanent-high\" data-severity=\"high\" open>");
        html.Should().Contain("<details class=\"area\" open><summary id=\"coverage\">");
    }

    [Fact]
    public void Render_RollsUpGroupFindings_WithOutlineDiagramAndMemberTable()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<div class=\"group\" data-search=\"");
        html.Should().Contain("<span class=\"who\">Tier 0 Admins</span>");
        html.Should().Contain("<ul class=\"tree\">");
        html.Should().Contain("<svg class=\"nesting\"");
        html.Should().Contain("<details class=\"members\" open><summary>");
        html.Should().Contain("Casey Wong");
    }

    [Fact]
    public void Render_DataSearch_IsLowerCase_AndCoversPrincipalRoleScopeAndPath()
    {
        var html = HtmlRenderer.Render(Sample());

        var values = Regex.Matches(html, "data-search=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        values.Should().NotBeEmpty();
        values.Should().OnlyContain(v => v == v.ToLowerInvariant());
        values.Should().Contain(v => v.Contains("casey.wong@contoso.com") && v.Contains("global administrator") && v.Contains("tier 0 admins"));
    }

    [Fact]
    public void Render_Coverage_ListsEverySourceWithState()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().Contain("<td>Azure management groups</td><td><span class=\"pill skipped\">skipped</span></td>");
        html.Should().Contain("<td>Entra role assignments</td><td><span class=\"pill read\">read</span></td>");
        html.Should().NotContain("Some sources were skipped.", "the notice box is replaced by the Coverage tile and section");
    }

    [Fact]
    public void Render_NotScannedArea_ShowsTheReason()
    {
        var snapshot = SnapshotBuilder.Contoso().User("u1", "A", "a@contoso.com").Assigned("a1", "u1", "rd-ga").Skipped("azure", "skipped with --skip-azure").Build();
        var findings = RuleRunner.Run(snapshot, new AuditOptions());

        var html = HtmlRenderer.Render(AuditReport.From(snapshot, findings, findings, new AuditOptions(), "x", []));

        html.Should().Contain("<span class=\"pill\">Not scanned</span>");
        html.Should().Contain("skipped with --skip-azure");
    }

    [Fact]
    public void Render_EmptyReport_ReadsClean()
    {
        var snapshot = SnapshotBuilder.Contoso().Build();

        var html = HtmlRenderer.Render(AuditReport.From(snapshot, [], [], new AuditOptions(), "x", []));

        html.Should().Contain("No standing privileged access was found in Contoso.");
        html.Should().Contain("<span class=\"pill clean\">Clean</span>");
        html.Should().Contain("Every source was read.");
    }
```

Delete `Render_ARuleWithTwoSeverities_YieldsTwoSections` only if it fails on the new id format; it should still pass because rule-block ids are unchanged, so keep it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~HtmlRendererTests"`
Expected: the new tests fail (old structure), the golden test fails.

- [ ] **Step 3: Rewrite `Render`**

Replace the `Render` method and the private helpers `Meta`, `Card`, `Humanize`, `Date` in `HtmlRenderer.cs` with the following (keep the resource plumbing from Task 5 and `E`):

```csharp
    private const string Caret = "<svg class=\"caret\" viewBox=\"0 0 20 20\" fill=\"none\" stroke=\"#526176\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M8 6l4 4-4 4\"/></svg>";

    private static readonly (string Source, string Name, string Note)[] CoverageSources =
    [
        ("entra", "Entra role assignments", "Every active assignment and eligibility."),
        ("groups", "Groups", "Members of role-assigned groups, nested groups included."),
        ("pim-for-groups", "PIM for Groups", "Active and eligible memberships of PIM-managed groups."),
        ("azure", "Azure subscriptions", "Role assignments in the subscriptions the account can read."),
        ("azure-management-groups", "Azure management groups", "Role assignments inherited from management groups."),
        ("principals", "Principals", "Names, sign-in names and guest status."),
    ];

    public static string Render(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var b = new StringBuilder(128 * 1024);
        var tenantName = report.Tenant.DisplayName ?? report.Tenant.Id;
        b.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n<meta name=\"robots\" content=\"noindex\" />\n");
        b.Append("<title>").Append(E($"Standing access report — {tenantName}")).Append("</title>\n<style>\n").Append(Stylesheet).Append("\n</style>\n</head>\n<body>\n");

        Header(b, report, tenantName);

        b.Append("<main class=\"wrap\">\n");
        StartHere(b, report);
        var byArea = report.Findings.GroupBy(f => ReportAreas.Of(f.Id)).ToDictionary(g => g.Key, g => (IReadOnlyList<Finding>)g.ToList());
        foreach (var area in ReportAreas.Ordered)
        {
            var findings = byArea.GetValueOrDefault(area) ?? [];
            if (area == ReportAreas.Other && findings.Count == 0)
            {
                continue;
            }

            Area(b, area, findings, report.Skipped);
        }

        Coverage(b, report.Skipped);
        Appendix(b, report);
        b.Append("</main>\n<footer class=\"wrap\">Generated by <a href=\"https://elevate.reothor.no/audit.html\">elevate-audit</a>, the standing-access companion to Elevate.")
         .Append(report.Tool.Version == "sample" ? " Sample data is fictional." : string.Empty)
         .Append("</footer>\n<script>\n").Append(Script.TrimEnd()).Append("\n</script>\n</body>\n</html>\n");
        return b.ToString();
    }

    private static void Header(StringBuilder b, AuditReport report, string tenantName)
    {
        b.Append("<header class=\"report-header\"><div class=\"wrap\">\n<p class=\"eyebrow\">elevate-audit · standing privileged access</p>\n");
        b.Append("<h1>").Append(E(tenantName)).Append("</h1>\n<ul class=\"meta\">\n");
        Meta(b, "Tenant id", report.Tenant.Id);
        Meta(b, "Scanned by", report.Account);
        Meta(b, "Scanned at", report.ScannedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        Meta(b, "Tool", $"{report.Tool.Name} {report.Tool.Version}");
        b.Append("</ul>\n");

        var verdict = ReportAreas.VerdictFor(tenantName, report.AllFindings, report.Skipped);
        b.Append("<p class=\"verdict\"><strong>").Append(E(verdict.Head)).Append("</strong>");
        if (verdict.Tail is not null)
        {
            b.Append(' ').Append(E(verdict.Tail));
        }

        b.Append("</p>\n");
        if (report.Hidden > 0)
        {
            b.Append("<p class=\"muted\">").Append(E($"{RenderText.Findings(report.Hidden)} below --min-severity {report.Options.MinSeverity} hidden")).Append("</p>\n");
        }

        b.Append("<div class=\"tiles\">\n");
        var all = report.AllFindings.GroupBy(f => ReportAreas.Of(f.Id)).ToDictionary(g => g.Key, g => (IReadOnlyList<Finding>)g.ToList());
        foreach (var area in ReportAreas.Ordered)
        {
            var findings = all.GetValueOrDefault(area) ?? [];
            if (area == ReportAreas.Other && findings.Count == 0)
            {
                continue;
            }

            Tile(b, area, findings, report.Skipped);
        }

        CoverageTile(b, report.Skipped);
        b.Append("</div>\n</div></header>\n");
    }

    private static void Tile(StringBuilder b, ReportArea area, IReadOnlyList<Finding> findings, IReadOnlyList<SkippedSource> skipped)
    {
        var state = ReportAreas.StateOf(area, findings, skipped);
        var pillClass = state switch
        {
            AreaState.Critical => "pill critical",
            AreaState.Attention => "pill attention",
            AreaState.Clean => "pill clean",
            _ => "pill",
        };
        b.Append("<a class=\"tile\" href=\"#").Append(area.Id).Append("\"><div class=\"head\"><span class=\"name\">").Append(E(area.Name)).Append("</span><span class=\"").Append(pillClass).Append("\">").Append(ReportAreas.StateLabel(state)).Append("</span></div>");
        b.Append("<div class=\"counts\">");
        if (state != AreaState.NotScanned)
        {
            foreach (var severity in new[] { Severity.High, Severity.Medium, Severity.Low, Severity.Info })
            {
                var n = findings.Count(f => f.Severity == severity);
                if (n > 0)
                {
                    var s = severity.ToString().ToLowerInvariant();
                    b.Append("<span><span class=\"n ").Append(s).Append("\">").Append(n.ToString(CultureInfo.InvariantCulture)).Append("</span> <span class=\"unit\">").Append(s).Append("</span></span>");
                }
            }
        }

        if (ReportAreas.UnderCounts(area, skipped))
        {
            b.Append("<span class=\"pill skipped\">under-counts</span>");
        }

        b.Append("</div><p class=\"line\">").Append(E(ReportAreas.Sentence(area, findings, skipped))).Append("</p></a>\n");
    }

    private static void CoverageTile(StringBuilder b, IReadOnlyList<SkippedSource> skipped)
    {
        var n = skipped.Count;
        b.Append("<a class=\"tile\" href=\"#coverage\"><div class=\"head\"><span class=\"name\">Coverage</span>")
         .Append(n == 0 ? "<span class=\"pill clean\">Complete</span>" : $"<span class=\"pill skipped\">{n} skipped</span>").Append("</div><div class=\"counts\">");
        if (n > 0)
        {
            b.Append("<span><span class=\"n skipped\">").Append(n.ToString(CultureInfo.InvariantCulture)).Append("</span> <span class=\"unit\">skipped</span></span>");
        }

        b.Append("</div><p class=\"line\">").Append(E(ReportAreas.Sentence(ReportAreas.Coverage, [], skipped))).Append("</p></a>\n");
    }

    private static void StartHere(StringBuilder b, AuditReport report)
    {
        var high = report.Findings.Where(f => f.Severity == Severity.High).ToList();
        b.Append("<section class=\"section-title\" id=\"start-here\"><div><h2>Start here</h2>");
        if (high.Count == 0)
        {
            b.Append("<p class=\"muted\">No high-severity findings. ").Append(E(report.Findings.Count == 0 ? "No standing privileged access was found." : "Review the medium and low findings below.")).Append("</p></div></section>\n");
            return;
        }

        b.Append("<p class=\"muted\">The high findings, grouped so one action fixes many.</p></div></section>\n<ol class=\"start\">\n");
        foreach (var group in high.GroupBy(f => f.Id))
        {
            if (GroupRollup.RollsUp(group.Key))
            {
                var (cards, rows) = GroupRollup.Build(group.Key, group.ToList());
                foreach (var card in cards)
                {
                    b.Append("<li data-search=\"").Append(E(Search(card))).Append("\" data-severity=\"high\"><span class=\"who\">").Append(E(card.Group.DisplayName)).Append(" · ").Append(E(card.Role.DisplayName)).Append(" on ").Append(E(card.Scope.DisplayName))
                     .Append(" <span class=\"muted\">· ").Append(E(ReportAreas.Plural(card.People, "person", "people"))).Append(card.NestedGroups > 0 ? E($" through {ReportAreas.Plural(card.NestedGroups, "nested group", "nested groups")}") : string.Empty).Append("</span></span>");
                    b.Append("<span class=\"what\">").Append(E(card.Remedy)).Append(" <a href=\"").Append(E(card.PortalUrl)).Append("\">Open in portal</a></span></li>\n");
                }

                foreach (var f in rows)
                {
                    StartItem(b, f);
                }
            }
            else
            {
                foreach (var f in group)
                {
                    StartItem(b, f);
                }
            }
        }

        b.Append("</ol>\n");
    }

    private static void StartItem(StringBuilder b, Finding f)
    {
        b.Append("<li data-search=\"").Append(E(Search(f))).Append("\" data-severity=\"high\"><span class=\"who\">").Append(E(f.Principal.DisplayName)).Append(f.Principal.IsGuest ? " <span class=\"pill\">guest</span>" : string.Empty)
         .Append(" · ").Append(E(f.Role.DisplayName)).Append(" on ").Append(E(f.Scope.DisplayName)).Append("</span>");
        if (f.Via.Count > 0)
        {
            b.Append("<span class=\"path\">via ").Append(E(string.Join(" ← ", f.Via.Select(v => v.DisplayName)))).Append("</span>");
        }

        b.Append("<span class=\"what\">").Append(E(f.Remedy)).Append(" <a href=\"").Append(E(f.PortalUrl)).Append("\">Open in portal</a></span></li>\n");
    }

    private static void Area(StringBuilder b, ReportArea area, IReadOnlyList<Finding> findings, IReadOnlyList<SkippedSource> skipped)
    {
        var open = findings.Any(f => f.Severity == Severity.High);
        b.Append("<details class=\"area\"").Append(open ? " open" : string.Empty).Append("><summary id=\"").Append(area.Id).Append("\">").Append(Caret).Append("<h2>").Append(E(area.Name)).Append("</h2>");
        foreach (var severity in new[] { Severity.High, Severity.Medium, Severity.Low, Severity.Info })
        {
            var n = findings.Count(f => f.Severity == severity);
            if (n > 0)
            {
                var s = severity.ToString().ToLowerInvariant();
                b.Append("<span class=\"pill ").Append(s).Append("\">").Append(n.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(s).Append("</span>");
            }
        }

        if (ReportAreas.NotScannedReason(area, skipped) is not null)
        {
            b.Append("<span class=\"pill\">Not scanned</span>");
        }

        if (ReportAreas.UnderCounts(area, skipped))
        {
            b.Append("<span class=\"pill skipped\">under-counts</span>");
        }

        b.Append("</summary>\n<p class=\"area-desc\">").Append(E(area.Description));
        if (ReportAreas.NotScannedReason(area, skipped) is { } reason)
        {
            b.Append(' ').Append(E(reason));
        }

        b.Append("</p>\n");
        if (findings.Count == 0)
        {
            b.Append("<p class=\"area-desc\">").Append(E(ReportAreas.Sentence(area, findings, skipped))).Append("</p>\n");
        }

        foreach (var group in findings.GroupBy(f => (f.Id, f.Severity)))
        {
            Rule(b, group.Key.Id, group.Key.Severity, group.ToList(), skipped);
        }

        b.Append("</details>\n");
    }

    private static void Rule(StringBuilder b, string code, Severity severity, IReadOnlyList<Finding> findings, IReadOnlyList<SkippedSource> skipped)
    {
        var s = severity.ToString().ToLowerInvariant();
        var (cards, rows) = GroupRollup.RollsUp(code) ? GroupRollup.Build(code, findings) : ([], findings);
        var count = cards.Count > 0
            ? $"{ReportAreas.Plural(cards.Count, "group", "groups")} · {ReportAreas.Plural(cards.Sum(c => c.People), "person", "people")}{(rows.Count > 0 ? $" · {ReportAreas.Plural(rows.Count, "direct finding", "direct findings")}" : string.Empty)}"
            : RenderText.Findings(findings.Count);
        b.Append("<details class=\"rule\" id=\"").Append(E(code.ToLowerInvariant())).Append('-').Append(s).Append("\" data-severity=\"").Append(s).Append("\" open><summary>").Append(Caret)
         .Append("<h3>").Append(E(code)).Append("</h3><span class=\"pill ").Append(s).Append("\">").Append(s).Append("</span><span class=\"desc\">").Append(E(RuleDescription(code))).Append("</span><span class=\"count\">").Append(E(count)).Append("</span></summary>\n");

        var unreadable = skipped.Any(x => string.Equals(x.Source, "groups", StringComparison.OrdinalIgnoreCase) && x.Reason.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
        foreach (var card in cards)
        {
            Card(b, card, unreadable);
        }

        if (rows.Count > 0)
        {
            Table(b, rows);
        }

        b.Append("</details>\n");
    }

    private static void Card(StringBuilder b, RollupCard card, bool unreadableGroups)
    {
        b.Append("<div class=\"group\" data-search=\"").Append(E(Search(card))).Append("\"><div class=\"title\"><span class=\"who\">").Append(E(card.Group.DisplayName)).Append("</span><span class=\"pill\">group</span><span class=\"muted\">grants</span><span class=\"who\">").Append(E(card.Role.DisplayName)).Append("</span><span class=\"muted\">on ").Append(E(card.Scope.DisplayName)).Append(" to ").Append(E(ReportAreas.Plural(card.People, "person", "people")))
         .Append(card.NestedGroups > 0 ? E($" through {ReportAreas.Plural(card.NestedGroups, "nested group", "nested groups")}") : " directly").Append("</span></div>\n");
        b.Append("<p class=\"what\">").Append(E(card.Remedy)).Append(" <a href=\"").Append(E(card.PortalUrl)).Append("\">Open in portal</a></p>\n");
        if (unreadableGroups)
        {
            b.Append("<p class=\"what\">Some nested groups could not be read; their members are missing.</p>\n");
        }

        var diagram = GroupRollup.Diagram(card);
        b.Append("<div class=\"body\"><div><span class=\"label\">Membership</span>").Append(GroupRollup.Outline(card)).Append("</div>");
        if (diagram is not null)
        {
            b.Append("<div class=\"diagram\"><span class=\"label\">How the groups nest</span>").Append(diagram).Append("</div>");
        }

        b.Append("</div>\n");
        if (card.Members.Count > 0)
        {
            b.Append("<details class=\"members\" open><summary>").Append(Caret).Append("People reached (").Append(card.Members.Count.ToString(CultureInfo.InvariantCulture)).Append(")</summary>\n<div class=\"table-scroll\"><table><thead><tr><th>Person</th><th>Through</th><th>Remedy</th></tr></thead><tbody>\n");
            foreach (var f in card.Members)
            {
                b.Append("<tr data-search=\"").Append(E(Search(f))).Append("\"><td>").Append(E(f.Principal.DisplayName)).Append(f.Principal.IsGuest ? " <span class=\"pill\">guest</span>" : string.Empty);
                if (f.Principal.UserPrincipalName is { } upn)
                {
                    b.Append("<br /><span class=\"muted\">").Append(E(upn)).Append("</span>");
                }

                b.Append("</td><td class=\"path\">").Append(f.Via.Count == 0 ? "direct" : E(string.Join(" ← ", f.Via.Select(v => v.DisplayName)))).Append("</td><td>").Append(E(f.Remedy)).Append(" <a href=\"").Append(E(f.PortalUrl)).Append("\">Open in portal</a></td></tr>\n");
            }

            b.Append("</tbody></table></div></details>\n");
        }

        b.Append("</div>\n");
    }

    private static void Table(StringBuilder b, IReadOnlyList<Finding> rows)
    {
        b.Append("<div class=\"table-scroll\"><table><thead><tr><th>Principal</th><th>Role</th><th>Scope</th><th>How</th><th>Remedy</th></tr></thead><tbody>\n");
        foreach (var f in rows)
        {
            b.Append("<tr data-search=\"").Append(E(Search(f))).Append("\"><td>").Append(E(f.Principal.DisplayName));
            if (f.Principal.UserPrincipalName is { } upn)
            {
                b.Append("<br /><span class=\"muted\">").Append(E(upn)).Append("</span>");
            }

            if (f.Principal.Type == PrincipalType.Group)
            {
                b.Append(" <span class=\"pill\">group</span>");
            }

            if (f.Principal.IsGuest)
            {
                b.Append(" <span class=\"pill\">guest</span>");
            }

            b.Append("</td><td>").Append(E(f.Role.DisplayName)).Append("<br /><span class=\"muted\">").Append(E(f.Role.System.ToString())).Append("</span></td>");
            b.Append("<td>").Append(E(f.Scope.DisplayName)).Append("<br /><span class=\"muted\">").Append(E(Humanize(f.Scope.Kind))).Append("</span></td>");
            b.Append("<td>");
            if (f.Via.Count == 0)
            {
                b.Append("<span class=\"muted\">direct</span>");
            }
            else
            {
                b.Append("<details><summary>through ").Append(f.Via.Count.ToString(CultureInfo.InvariantCulture)).Append(f.Via.Count == 1 ? " group" : " groups").Append("</summary><span class=\"path\">").Append(E(string.Join(" ← ", f.Via.Select(v => v.DisplayName)))).Append("</span></details>");
            }

            b.Append("</td><td>").Append(E(f.Remedy)).Append(" <a href=\"").Append(E(f.PortalUrl)).Append("\">Open in portal</a></td></tr>\n");
        }

        b.Append("</tbody></table></div>\n");
    }

    private static void Coverage(StringBuilder b, IReadOnlyList<SkippedSource> skipped)
    {
        b.Append("<details class=\"area\"").Append(skipped.Count > 0 ? " open" : string.Empty).Append("><summary id=\"coverage\">").Append(Caret).Append("<h2>Coverage</h2>")
         .Append(skipped.Count == 0 ? "<span class=\"pill clean\">Complete</span>" : $"<span class=\"pill skipped\">{skipped.Count} skipped</span>").Append("</summary>\n<p class=\"area-desc\">").Append(E(ReportAreas.Coverage.Description)).Append("</p>\n");
        b.Append("<div class=\"table-scroll\"><table><thead><tr><th>Source</th><th>State</th><th>What it means</th></tr></thead><tbody>\n");
        var azureSkipped = ReportAreas.Has(skipped, "azure");
        foreach (var (source, name, note) in CoverageSources)
        {
            var reasons = skipped.Where(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase)).Select(s => s.Reason).ToList();
            if (source == "azure-management-groups" && azureSkipped && reasons.Count == 0)
            {
                reasons.Add("Not scanned because Azure was skipped.");
            }

            b.Append("<tr><td>").Append(E(name)).Append("</td><td>").Append(reasons.Count == 0 ? "<span class=\"pill read\">read</span>" : "<span class=\"pill skipped\">skipped</span>")
             .Append("</td><td class=\"muted\">").Append(E(reasons.Count == 0 ? note : string.Join(" ", reasons))).Append("</td></tr>\n");
        }

        b.Append("</tbody></table></div>\n</details>\n");
    }

    private static void Appendix(StringBuilder b, AuditReport report)
    {
        b.Append("<details class=\"appendix\" id=\"appendix\"><summary>").Append(Caret).Append("<h2>Appendix</h2><span class=\"muted\">Options, scopes requested, evidence ids</span></summary>\n<p>Options: all roles ").Append(report.Options.AllRoles ? "on" : "off").Append("; minimum severity ").Append(E(report.Options.MinSeverity)).Append("; ignored rules: ").Append(report.Options.Ignored.Count == 0 ? "none" : E(string.Join(", ", report.Options.Ignored))).Append(".</p>\n");
        b.Append("<p>Read-only Microsoft Graph scopes requested: ").Append(report.ScopesRequested.Count == 0 ? "none recorded" : string.Join(", ", report.ScopesRequested.Select(s => "<code>" + E(s) + "</code>"))).Append(". Nothing was written to the tenant.</p>\n");
        b.Append("<p>This report contains personal data (names and sign-in names of people who hold roles). Handle it as you would any access review.</p>\n<details><summary>Evidence ids</summary><div class=\"table-scroll\"><table><thead><tr><th>Rule</th><th>Principal id</th><th>Assignment id</th><th>Start</th><th>End</th><th>Type</th></tr></thead><tbody>\n");
        foreach (var f in report.Findings)
        {
            b.Append("<tr><td>").Append(E(f.Id)).Append("</td><td><code>").Append(E(f.Principal.Id)).Append("</code></td><td><code>").Append(E(f.Evidence.AssignmentId)).Append("</code></td><td>").Append(E(Date(f.Evidence.StartDateTime))).Append("</td><td>").Append(E(Date(f.Evidence.EndDateTime))).Append("</td><td>").Append(E(f.Evidence.AssignmentType?.ToString() ?? f.Evidence.MemberType ?? "—")).Append("</td></tr>\n");
        }

        b.Append("</tbody></table></div></details></details>\n");
    }

    private static string RuleDescription(string code) => code.ToUpperInvariant() switch
    {
        "ENTRA-USER-PERMANENT" => "A user holds a permanent active Entra role directly",
        "ENTRA-GROUP-PERMANENT" => "A group holds a permanent active Entra role",
        "ENTRA-GROUP-NOT-PIM" => "A role-assignable group carrying a role is not onboarded to PIM for Groups",
        "ENTRA-GROUP-NOT-ASSIGNABLE" => "A role is assigned to a group that is not role-assignable",
        "GROUP-MEMBER-PERMANENT" => "A group managed by PIM for Groups still has a permanent member or owner",
        "AZURE-PERMANENT" => "A permanent privileged Azure role assignment",
        "SP-PERMANENT" => "A service principal or managed identity holds a permanent privileged role",
        "GUEST-PERMANENT" => "A guest holds a permanent privileged role",
        "ELIGIBLE-NO-END" => "An eligibility has no end date",
        "GA-COUNT" => "Fewer than 2 or more than 5 people can become Global Administrator",
        _ => string.Empty,
    };

    private static string Search(Finding f) =>
        string.Join(' ', new[] { f.Principal.DisplayName, f.Principal.UserPrincipalName, f.Role.DisplayName, f.Scope.DisplayName }.Concat(f.Via.Select(v => v.DisplayName)).Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();

    private static string Search(RollupCard card) =>
        string.Join(' ', new[] { card.Group.DisplayName, card.Role.DisplayName, card.Scope.DisplayName }.Concat(card.Members.Select(Search))).ToLowerInvariant();

    private static void Meta(StringBuilder b, string label, string value) =>
        b.Append("<li>").Append(E(label)).Append("<strong>").Append(E(value)).Append("</strong></li>\n");

    private static string Humanize(ScopeKind kind) => kind switch
    {
        ScopeKind.AdministrativeUnit => "administrative unit",
        ScopeKind.ManagementGroup => "management group",
        ScopeKind.ResourceGroup => "resource group",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Date(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";
```

Notes for the executor:
- `ReportAreas.Has` is `internal static` from Task 1; the renderer is in the same assembly.
- The `(cards, rows)` tuple deconstruction with `([], findings)` needs the tuple typed; if the compiler complains, write `GroupRollup.RollsUp(code) ? GroupRollup.Build(code, findings) : (Array.Empty<RollupCard>(), findings)`.
- Search values are lower-cased and encoded; the script compares against `getAttribute`, which decodes entities, so `E(Search(...))` is correct.
- `class="pill"` alone (no state class) is used for **Not scanned** and **Review** so the neutral mist style applies.

- [ ] **Step 4: Run the renderer tests**

Run: `dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~HtmlRendererTests"`
Expected: everything except `SampleReport_MatchesTheCommittedSitePage` passes. If `Render_Tiles_ShowStateAndUnderCounts` fails on "1 skipped", confirm `SampleSnapshot` still has exactly one skipped source.

- [ ] **Step 5: Regenerate the sample page and inspect it**

```bash
ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln --filter "FullyQualifiedName~SampleReport_MatchesTheCommittedSitePage"
git diff --stat site/audit-sample.html
```

Open `site/audit-sample.html` in a browser (`open site/audit-sample.html` on macOS) and check: verdict and seven tiles; clicking a tile scrolls and opens its area; the toolbar filters; the Tier 0 Admins card shows the outline and the diagram; print preview opens everything. Then run the whole suite:

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add audit/src/Elevate.Audit/Rendering/HtmlRenderer.cs audit/tests/Elevate.Audit.Tests/HtmlRendererTests.cs site/audit-sample.html
git commit -m "Audit: rebuild the HTML report around an executive summary, areas and group roll-ups"
```

---

### Task 8: Deeper nesting in the sample, docs

**Files:**
- Modify: `audit/tests/Elevate.Audit.Tests/Support/SampleSnapshot.cs`
- Modify: `docs/audit.md` §6
- Regenerate: `site/audit-sample.html`, `audit/tests/Elevate.Audit.Tests/Golden/sample-report.json`

The sample's Tier 0 Admins → Platform Team → Casey Wong is two levels. Move Casey one level down so the sample shows a three-level chain, without changing any count (the three tests pinning "13 high" must keep passing).

- [ ] **Step 1: Deepen the sample**

In `SampleSnapshot.cs` change the Platform Team group and add one group:

```csharp
        .Group("g-platform", "Platform Team", assignable: false, members: [("g-oncall", PrincipalType.Group), ("g-tier0", PrincipalType.Group)])
        .Group("g-oncall", "Platform On-call", assignable: false, members: [("u-casey", PrincipalType.User)])
```

- [ ] **Step 2: Run the suite and see what moves**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: only the two golden tests fail (`SampleReport_MatchesTheCommittedSitePage` and the JSON golden in `JsonAndSnapshotTests`); the `13 high` assertions still pass. If any count assertion fails, the membership change added a finding; check `Casey Wong` appears exactly once under `ENTRA-GROUP-PERMANENT` with `via` `Tier 0 Admins ← Platform Team ← Platform On-call`.

- [ ] **Step 3: Regenerate the goldens and review**

```bash
ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln
git diff audit/tests/Elevate.Audit.Tests/Golden/sample-report.json | head -60
dotnet test audit/Elevate.Audit.sln
```

Expected: the JSON diff touches only Casey's `via` and `remedy`; the full suite passes afterwards. Open `site/audit-sample.html` and confirm the diagram shows Tier 0 Admins → Platform Team → Platform On-call.

- [ ] **Step 4: Rewrite docs §6**

Replace section "## 6. Reading the report" in `docs/audit.md` (up to the "A sample report…" paragraph, which stays) with:

```markdown
## 6. Reading the report

The HTML report opens with a one-sentence verdict — how many people and workload identities
hold standing privileged access, and whether anything was skipped — and one tile per area:
Entra roles, PIM for Groups, Azure RBAC, Guests, Workload identities, Hygiene and Coverage. Each
tile shows a state (Critical, Attention, Review, Clean or Not scanned), the counts per severity,
and one line in plain words; click it to jump to the area. An area marked **under-counts** has a
partially skipped source behind it, listed under Coverage.

**Start here** lists the high findings, grouped so one action fixes many: a role-assigned group
appears once, with the number of people it reaches, instead of once per member.

Then one collapsible section per area, and inside it one block per rule and severity. Group
findings are rolled up into one card per group: who it grants what to, the remedy, a membership
outline showing the nested groups with counts, a small diagram of how the groups nest, and the
full list of people reached. Direct findings stay as table rows. The Coverage section lists every
source and whether it was read. The appendix holds the options used, the scopes requested and the
assignment ids for auditors.

With JavaScript on, a toolbar adds search across people, groups, roles and scopes, severity
filters (High and Medium on by default), and expand or collapse all; long tables show the first
50 rows with a "Show all" button. With JavaScript off the report is the same page without the
toolbar, every row visible. The report loads nothing from the network either way, and prints
with every section open.

Remedies are the standard PIM moves: convert a permanent assignment to eligible, onboard a group
to PIM for Groups, replace a dynamic or non-role-assignable group with a static role-assignable
one, or, for service principals, review the workload identity's need for the role.
```

- [ ] **Step 5: Commit**

```bash
git add audit/tests/Elevate.Audit.Tests/Support/SampleSnapshot.cs audit/tests/Elevate.Audit.Tests/Golden/sample-report.json site/audit-sample.html docs/audit.md
git commit -m "Audit: three-level nesting in the sample report; document the redesigned report"
```

---

## Self-review against the spec

- §2 areas, Other, skipped effects → Task 1. §3 verdict, tiles, sentences, unfiltered counts → Tasks 2, 5, 7. §4 body structure, ids, descriptions, Coverage table → Task 7. §5 roll-ups, card, outline, diagram, Start here, unreadable note → Tasks 3, 4, 7 (spec amended in Task 3 for `AZURE-PERMANENT` and per-parent trie nodes). §6 script → Task 5 (`report.js`) with CSS hooks in Task 6; `hidden=until-found` is not used. §7 styling → Task 6. §8 files → all tasks; `ReportSentences` lives inside `ReportAreas`, as §8 allows. §9 tests → Tasks 1–5, 7; the "no `hidden` attribute" assertion is in Task 7 step 1. §10 out of scope untouched.
- Names used consistently: `ReportAreas.Of/StateOf/StateLabel/Sentence/VerdictFor/SkippedClause/UnderCounts/NotScannedReason/Plural/Has`, `GroupRollup.RollsUp/Build/Tree/Outline/Diagram/MaxDiagramGroups/MaxInlinePeople`, `RollupCard.People/NestedGroups`, `AuditReport.AllFindings`, `HtmlRenderer.Script`.
