# Access Packages: Core Port and CLI Commands Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the access package layer of the Swift `ElevateCore` (PR #90) to the shared C# `Elevate.Core` (GitHub issue #91), then expose it in the `elevate` CLI as `elevate packages list|requests|assigned|request|cancel` (issue #93).

**Architecture:** `windows/src/Elevate.Core` gains the same public surface as the Swift Core: an `EntitlementAll` scope set, `AccessTokenClaims.PermitsEntitlementSelfService`, `TenantContext.AccessPackagesAvailable`, the access package models, an `AccessPackageProvider` over `GraphTransport` (Graph v1.0), two pure value types (`AccessPackageDiff`, `NewRoleTracker`) and two record lists on `AppState` that keep `state.json` readable by both ports. The CLI adds an `ElevateSession.AccessPackages` partial that wraps the provider with the session's interactive retry, DTOs and tables in `Views`, and a `packages` command group. No polling, no notifications, no persistence of snapshots in the CLI.

**Tech Stack:** .NET 10 (`dotnet` 10.0.400 on this Mac), C# 13, System.Text.Json with the project's `Json.Options` (strict, for state) and `Json.LenientOptions` / `GraphJson.Options` (for Graph DTOs), xUnit + FluentAssertions 7, System.CommandLine 2.0.11, Spectre.Console 0.57.

**Spec:** `docs/superpowers/specs/2026-09-08-elevate-access-packages-design.md` (sections 2 and 3 for the Core; the CLI shape comes from issue #93). Swift sources to mirror live under `macos/Sources/ElevateCore` and `macos/Tests/ElevateCoreTests`.

## Global Constraints

- Work on branch `access-packages-core-cli`, created from `origin/main` (commit `4162783` or later). `main` is protected; merge by PR. The PR closes #91 and #93.
- Core tests: `dotnet test windows/Elevate.sln` (runs on macOS; the WinUI app project is excluded from the solution build off Windows, so the whole solution builds here). Every Core task leaves the suite green.
- CLI tests: `dotnet test cli/Elevate.Cli.sln`. The CLI references `../windows/src/Elevate.Core` directly, so a Core change is immediately visible to the CLI.
- Both test projects build with warnings as errors. Public methods take `ArgumentNullException.ThrowIfNull` for reference parameters, like the existing providers.
- The only new Graph scope is `https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite`; its bare claim name is `EntitlementMgmt-SubjectAccess.ReadWrite`. Never add `EntitlementManagement.Read.All` or `EntitlementManagement.ReadWrite.All`.
- Graph base is `GraphTransport.GraphUrl` (v1.0). Never the beta base.
- `state.json` interop: persisted values are arrays of records with a `tenantKey` field, camelCase keys, enums as camelCase strings (`"pendingApproval"`), dates as `yyyy-MM-ddTHH:mm:ssZ`. These come for free from `Json.Options`; do not add converters.
- Wire DTOs are private positional records decoded with `GraphJson.Options` (lenient), models are public records; the CLI and app never see Graph shapes.
- Fixtures: the eight `ap-*.json` files are copied verbatim from `macos/Tests/ElevateCoreTests/Fixtures` into `windows/tests/Elevate.Core.Tests/Fixtures` (the test csproj already globs `Fixtures\**\*.json`).
- Sample data uses `alex.rivera@contoso.com` and `alex@contoso.com`, never the maintainer's address.
- No attribution lines in commit messages. Commit message style: `Core: …`, `CLI: …`, `Docs: …`.
- Commands: run everything from the repo root `/Users/frode.hus/pimtray` with absolute or root-relative paths (the shell's working directory drifts).

---

## File map

Core (`windows/src/Elevate.Core`):

- Modify `Auth/TokenProviding.cs`: `Scopes.EntitlementAll`, `Scopes.EntitlementClaim`.
- Modify `Auth/AccessTokenClaims.cs`: `PermitsEntitlementSelfService`.
- Modify `Models/Identity.cs`: `TenantContext.AccessPackagesAvailable`.
- Create `Models/AccessPackages.cs`: `AccessPackage`, `AccessPackageRequestState` + `AccessPackageRequestStates`, `AccessPackageRequest`, `AccessPackageAssignmentState` + `AccessPackageAssignmentStates`, `AccessPackageAssignment`, `PolicyRequirement`, `AccessPackageSnapshot`.
- Create `Providers/AccessPackageProvider.cs`: `IAccessPackageProvider`, `AccessPackageProvider`.
- Create `Coordination/AccessPackageDiff.cs`: `AccessPackageEvent`, `AccessPackageDiff`.
- Create `Coordination/NewRoleTracker.cs`: `NewRoleTracker`.
- Modify `Storage/AppState.cs`: `AccessPackageRecord`, `RoleTrackingRecord`, the two lists and their helpers.

Core tests (`windows/tests/Elevate.Core.Tests`): `AccessPackageModelsTests.cs`, `AccessPackageProviderTests.cs`, `AccessPackageDiffTests.cs`, `NewRoleTrackerTests.cs`, additions to `AccessTokenClaimsTests.cs`, `AppStateStoreTests.cs` and `Fixtures/state-macos.json` (plus the assertions in `AppStateGoldenTests.cs`); fixtures `Fixtures/ap-*.json`.

CLI (`cli/src/Elevate.Cli`):

- Create `Session/ElevateSession.AccessPackages.cs`: the session methods.
- Modify `Session/ElevateSession.cs`: `Packages` property and constructor parameter.
- Modify `Selection/ShortId.cs`: `For(TenantKey, string)`.
- Modify `Rendering/Views.cs`: `Dto.Package`, `Dto.PackageRequest`, `Dto.PackageAssignment`, `Dto.PolicyOption`, the mappers and the three tables.
- Create `Commands/PackageCommands.cs`: the `packages` command group.
- Modify `Program.cs`: register `PackageCommands.Packages()`.

CLI tests (`cli/tests/Elevate.Cli.Tests`): `Support/Fakes.cs` (`FakeAccessPackageProvider`), `Support/TestSession.cs`, `PackageSessionTests.cs`, additions to `ViewsTests.cs` and `CommandTreeTests.cs`.

Docs: `cli/README.md`, `CHANGELOG.md`, `windows/CONTINUING.md`.

---

### Task 0: Branch

- [ ] **Step 1: Create the branch from main**

```bash
cd /Users/frode.hus/pimtray && git fetch -q origin main && git checkout -b access-packages-core-cli origin/main
```

- [ ] **Step 2: Confirm both suites are green before any change**

```bash
cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln 2>&1 | tail -5 && dotnet test cli/Elevate.Cli.sln 2>&1 | tail -5
```

Expected: both report `Passed!` with 0 failed.

---

### Task 1: Entitlement scope, claim check and tenant flag

**Files:**
- Modify: `windows/src/Elevate.Core/Auth/TokenProviding.cs`
- Modify: `windows/src/Elevate.Core/Auth/AccessTokenClaims.cs`
- Modify: `windows/src/Elevate.Core/Models/Identity.cs`
- Test: `windows/tests/Elevate.Core.Tests/AccessTokenClaimsTests.cs`, `windows/tests/Elevate.Core.Tests/AppStateStoreTests.cs`

**Interfaces:**
- Produces: `Scopes.EntitlementAll : IReadOnlyList<string>`, `Scopes.EntitlementClaim : string`, `AccessTokenClaims.PermitsEntitlementSelfService(string accessToken) : bool?`, `TenantContext.AccessPackagesAvailable : bool?` (last positional parameter, default null).

- [ ] **Step 1: Write the failing tests**

Append to `AccessTokenClaimsTests.cs` inside the class:

```csharp
    [Fact]
    public void EntitlementScopeIsDetected()
    {
        AccessTokenClaims.PermitsEntitlementSelfService(Token("User.Read EntitlementMgmt-SubjectAccess.ReadWrite")).Should().BeTrue();
        AccessTokenClaims.PermitsEntitlementSelfService(Token("User.Read RoleAssignmentSchedule.ReadWrite.Directory")).Should().BeFalse();
        AccessTokenClaims.PermitsEntitlementSelfService("not-a-jwt").Should().BeNull();
        AccessTokenClaims.PermitsEntitlementSelfService(Token(null)).Should().BeNull();
    }

    [Fact]
    public void EntitlementScopeSetHoldsOneUserConsentableScope()
    {
        Scopes.EntitlementAll.Should().Equal("https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite");
        Scopes.EntitlementClaim.Should().Be("EntitlementMgmt-SubjectAccess.ReadWrite");
    }
```

Append to `AppStateStoreTests.cs` inside the class:

```csharp
    [Fact]
    public void AccessPackagesAvailableFlagRoundTripsAndDefaultsToNull()
    {
        var state = new AppState();
        state.UpsertTenant(new TenantContext("i", "t", "Home", TenantSource.Home, AccessPackagesAvailable: true));
        state.UpsertTenant(new TenantContext("i", "u", "Other", TenantSource.Manual));

        var json = Json.Serialize(state);
        json.Should().Contain("\"accessPackagesAvailable\":true");
        var back = Json.Deserialize<AppState>(json)!;
        back.Tenants[0].AccessPackagesAvailable.Should().BeTrue();
        back.Tenants[1].AccessPackagesAvailable.Should().BeNull();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AccessTokenClaimsTests|FullyQualifiedName~AccessPackagesAvailableFlag" 2>&1 | tail -15`
Expected: build error (`Scopes` has no `EntitlementAll`, `AccessTokenClaims` has no `PermitsEntitlementSelfService`, `TenantContext` has no `AccessPackagesAvailable`).

- [ ] **Step 3: Implement**

In `Auth/TokenProviding.cs`, add to `Scopes` after `GroupAll`:

```csharp
    /// <summary>
    /// Delegated Graph permission for self-service entitlement management (access packages).
    /// User-consentable: no admin consent is needed, unlike the PIM scopes above.
    /// </summary>
    public static IReadOnlyList<string> EntitlementAll { get; } = ["https://graph.microsoft.com/EntitlementMgmt-SubjectAccess.ReadWrite"];

    /// <summary>The bare scope name as it appears in a token's <c>scp</c> claim.</summary>
    public const string EntitlementClaim = "EntitlementMgmt-SubjectAccess.ReadWrite";
```

In `Auth/AccessTokenClaims.cs`, add after `PermitsEntraActivation`:

```csharp
    /// <summary>
    /// Whether a Graph token carries the self-service entitlement management scope. Null when the
    /// token does not expose its scopes.
    /// </summary>
    public static bool? PermitsEntitlementSelfService(string accessToken) =>
        GrantedScopes(accessToken) is { } scopes ? scopes.Contains(Scopes.EntitlementClaim) : null;
```

In `Models/Identity.cs`, add a last positional parameter to `TenantContext` after `GroupsUnavailableReason`:

```csharp
    // Set when group PIM reads are not permitted in this tenant (missing admin consent).
    string? GroupsUnavailableReason = null,
    // Whether the Graph token in this tenant carries the entitlement management scope, so access
    // packages can be read. Null until a refresh has looked at a token.
    bool? AccessPackagesAvailable = null)
```

- [ ] **Step 4: Run the whole Core suite**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln 2>&1 | tail -5`
Expected: `Passed!`, no failures (the golden fixture has no flag, so it stays null and is omitted on write).

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add windows/src/Elevate.Core/Auth windows/src/Elevate.Core/Models/Identity.cs windows/tests/Elevate.Core.Tests/AccessTokenClaimsTests.cs windows/tests/Elevate.Core.Tests/AppStateStoreTests.cs && git commit -q -m "Core: entitlement scope, claim check and TenantContext.AccessPackagesAvailable"
```

---

### Task 2: Access package models

**Files:**
- Create: `windows/src/Elevate.Core/Models/AccessPackages.cs`
- Test: `windows/tests/Elevate.Core.Tests/AccessPackageModelsTests.cs`

**Interfaces:**
- Produces (namespace `Elevate.Core.Models`):
  - `sealed record AccessPackage(string Id, string DisplayName, string? Description = null, bool IsHidden = false)`
  - `enum AccessPackageRequestState { Submitted, PendingApproval, Delivering, Delivered, DeliveryFailed, Denied, Scheduled, Canceled, PartiallyDelivered, Unknown }`
  - `static class AccessPackageRequestStates`: `Parse(string? raw)`, extension `IsOpen()`, `IsDeclined()`, `IsCancellable()`
  - `sealed record AccessPackageRequest(string Id, string PackageId, string PackageName, string RequestType, AccessPackageRequestState State, string? Status = null, string? Justification = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? CompletedAt = null, string? PolicyId = null)`
  - `enum AccessPackageAssignmentState { Delivering, Delivered, Expired, Unknown }`, `static class AccessPackageAssignmentStates.Parse(string?)`
  - `sealed record AccessPackageAssignment(string Id, string PackageId, string PackageName, AccessPackageAssignmentState State, string? PolicyName = null, DateTimeOffset? ExpiresAt = null)`
  - `sealed record PolicyRequirement(string Id, string DisplayName, string? Description, bool IsApprovalRequired, bool RequiresAnswers)`
  - `sealed record AccessPackageSnapshot` with `Requests`, `Assignments` (`IReadOnlyList`, never null, sequence equality) and constructor `(IReadOnlyList<AccessPackageRequest>? requests = null, IReadOnlyList<AccessPackageAssignment>? assignments = null)`.

- [ ] **Step 1: Write the failing tests**

Create `windows/tests/Elevate.Core.Tests/AccessPackageModelsTests.cs`:

```csharp
using Elevate.Core.Models;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AccessPackageModelsTests</c>.</summary>
public class AccessPackageModelsTests
{
    [Fact]
    public void RequestStateParsesCaseInsensitivelyAndFallsBackToUnknown()
    {
        AccessPackageRequestStates.Parse("PendingApproval").Should().Be(AccessPackageRequestState.PendingApproval);
        AccessPackageRequestStates.Parse("delivered").Should().Be(AccessPackageRequestState.Delivered);
        AccessPackageRequestStates.Parse("partiallyDelivered").Should().Be(AccessPackageRequestState.PartiallyDelivered);
        AccessPackageRequestStates.Parse("somethingNew").Should().Be(AccessPackageRequestState.Unknown);
        AccessPackageRequestStates.Parse("3").Should().Be(AccessPackageRequestState.Unknown);
        AccessPackageRequestStates.Parse(null).Should().Be(AccessPackageRequestState.Unknown);
    }

    [Fact]
    public void RequestStateGroupsIntoTabs()
    {
        AccessPackageRequestState.PendingApproval.IsOpen().Should().BeTrue();
        AccessPackageRequestState.Scheduled.IsOpen().Should().BeTrue();
        AccessPackageRequestState.Delivered.IsOpen().Should().BeFalse();
        AccessPackageRequestState.Denied.IsDeclined().Should().BeTrue();
        AccessPackageRequestState.Canceled.IsDeclined().Should().BeTrue();
        AccessPackageRequestState.DeliveryFailed.IsDeclined().Should().BeTrue();
        AccessPackageRequestState.Submitted.IsDeclined().Should().BeFalse();
        AccessPackageRequestState.Submitted.IsCancellable().Should().BeTrue();
        AccessPackageRequestState.PendingApproval.IsCancellable().Should().BeTrue();
        AccessPackageRequestState.Delivering.IsCancellable().Should().BeFalse();
    }

    [Fact]
    public void AssignmentStateParses()
    {
        AccessPackageAssignmentStates.Parse("Delivered").Should().Be(AccessPackageAssignmentState.Delivered);
        AccessPackageAssignmentStates.Parse("expired").Should().Be(AccessPackageAssignmentState.Expired);
        AccessPackageAssignmentStates.Parse("nope").Should().Be(AccessPackageAssignmentState.Unknown);
        AccessPackageAssignmentStates.Parse(null).Should().Be(AccessPackageAssignmentState.Unknown);
    }

    [Fact]
    public void ModelsRoundTripThroughJsonWithSwiftKeys()
    {
        var request = new AccessPackageRequest("r", "p", "Pkg", "userAdd", AccessPackageRequestState.Denied, "Denied", "why",
            Fixtures.Date("2026-09-02T09:00:00Z"), Fixtures.Date("2026-09-02T09:03:00Z"), "pol");
        var assignment = new AccessPackageAssignment("a", "p", "Pkg", AccessPackageAssignmentState.Delivered, "All", Fixtures.Date("2027-01-01T00:00:00Z"));
        var snapshot = new AccessPackageSnapshot([request], [assignment]);

        var json = Json.Serialize(snapshot);
        json.Should().Contain("\"state\":\"denied\"").And.Contain("\"createdAt\":\"2026-09-02T09:00:00Z\"")
            .And.Contain("\"packageName\":\"Pkg\"").And.Contain("\"expiresAt\":\"2027-01-01T00:00:00Z\"");
        Json.Deserialize<AccessPackageSnapshot>(json).Should().Be(snapshot);

        var requirement = new PolicyRequirement("pol", "Engineers", null, true, false);
        Json.Deserialize<PolicyRequirement>(Json.Serialize(requirement)).Should().Be(requirement);
    }

    [Fact]
    public void SnapshotDefaultsToEmptyListsAndComparesByContent()
    {
        new AccessPackageSnapshot().Requests.Should().BeEmpty();
        new AccessPackageSnapshot().Assignments.Should().BeEmpty();
        new AccessPackageSnapshot().Should().Be(new AccessPackageSnapshot([], []));
        Json.Deserialize<AccessPackageSnapshot>("""{"requests":[],"assignments":[]}""").Should().Be(new AccessPackageSnapshot());
        new AccessPackageSnapshot([new AccessPackageRequest("r", "p", "Pkg", "userAdd", AccessPackageRequestState.Submitted)])
            .Should().NotBe(new AccessPackageSnapshot());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AccessPackageModelsTests" 2>&1 | tail -15`
Expected: build errors for the missing types.

- [ ] **Step 3: Implement**

Create `windows/src/Elevate.Core/Models/AccessPackages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Elevate.Core.Models;

/// <summary>An entitlement management access package the signed-in user may request.</summary>
public sealed record AccessPackage(string Id, string DisplayName, string? Description = null, bool IsHidden = false);

/// <summary>Graph's <c>accessPackageAssignmentRequestState</c>, plus <see cref="Unknown"/> for values this build has not seen.</summary>
public enum AccessPackageRequestState
{
    Submitted,
    PendingApproval,
    Delivering,
    Delivered,
    DeliveryFailed,
    Denied,
    Scheduled,
    Canceled,
    PartiallyDelivered,
    Unknown,
}

public static class AccessPackageRequestStates
{
    /// <summary>Case-insensitive; anything unrecognised is <see cref="AccessPackageRequestState.Unknown"/> so one new value never fails a page.</summary>
    public static AccessPackageRequestState Parse(string? raw) =>
        ParseName<AccessPackageRequestState>(raw) ?? AccessPackageRequestState.Unknown;

    /// <summary>Requested tab: the request has not reached a final state.</summary>
    public static bool IsOpen(this AccessPackageRequestState state) => state is
        AccessPackageRequestState.Submitted or AccessPackageRequestState.PendingApproval or AccessPackageRequestState.Delivering
        or AccessPackageRequestState.Scheduled or AccessPackageRequestState.PartiallyDelivered;

    /// <summary>Declined tab: the request ended without access.</summary>
    public static bool IsDeclined(this AccessPackageRequestState state) => state is
        AccessPackageRequestState.Denied or AccessPackageRequestState.DeliveryFailed or AccessPackageRequestState.Canceled;

    /// <summary>Graph accepts a cancel only before delivery starts.</summary>
    public static bool IsCancellable(this AccessPackageRequestState state) => state is
        AccessPackageRequestState.Submitted or AccessPackageRequestState.PendingApproval;

    /// <summary>Matches an enum member by name, ignoring case; never by numeric value.</summary>
    internal static T? ParseName<T>(string? raw)
        where T : struct, Enum
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        foreach (var value in Enum.GetValues<T>())
        {
            if (string.Equals(value.ToString(), raw, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>One of the signed-in user's own access package requests.</summary>
public sealed record AccessPackageRequest(
    string Id,
    string PackageId,
    string PackageName,
    string RequestType,
    AccessPackageRequestState State,
    // Graph's free-text status, shown for failed and canceled requests.
    string? Status = null,
    string? Justification = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? PolicyId = null);

public enum AccessPackageAssignmentState
{
    Delivering,
    Delivered,
    Expired,
    Unknown,
}

public static class AccessPackageAssignmentStates
{
    public static AccessPackageAssignmentState Parse(string? raw) =>
        AccessPackageRequestStates.ParseName<AccessPackageAssignmentState>(raw) ?? AccessPackageAssignmentState.Unknown;
}

/// <summary>An access package currently (or formerly) assigned to the signed-in user.</summary>
public sealed record AccessPackageAssignment(
    string Id,
    string PackageId,
    string PackageName,
    AccessPackageAssignmentState State,
    string? PolicyName = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>One policy the signed-in user may request a package under, from <c>getApplicablePolicyRequirements</c>.</summary>
public sealed record PolicyRequirement(
    // The policy id.
    string Id,
    string DisplayName,
    string? Description,
    bool IsApprovalRequired,
    // True when the policy asks questions; Elevate hands such requests to the My Access portal.
    bool RequiresAnswers);

/// <summary>What one poll of a tenant returned. Persisted so the next poll can diff against it.</summary>
public sealed record AccessPackageSnapshot
{
    [JsonConstructor]
    public AccessPackageSnapshot(IReadOnlyList<AccessPackageRequest>? requests = null, IReadOnlyList<AccessPackageAssignment>? assignments = null)
    {
        Requests = requests ?? [];
        Assignments = assignments ?? [];
    }

    public IReadOnlyList<AccessPackageRequest> Requests { get; init; }

    public IReadOnlyList<AccessPackageAssignment> Assignments { get; init; }

    public bool Equals(AccessPackageSnapshot? other) =>
        other is not null && Requests.SequenceEqual(other.Requests) && Assignments.SequenceEqual(other.Assignments);

    public override int GetHashCode() => HashCode.Combine(Requests.Count, Assignments.Count);
}
```

- [ ] **Step 4: Run the tests**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AccessPackageModelsTests" 2>&1 | tail -8`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add windows/src/Elevate.Core/Models/AccessPackages.cs windows/tests/Elevate.Core.Tests/AccessPackageModelsTests.cs && git commit -q -m "Core: access package models"
```

---

### Task 3: AccessPackageProvider

**Files:**
- Create: `windows/src/Elevate.Core/Providers/AccessPackageProvider.cs`
- Create: `windows/tests/Elevate.Core.Tests/Fixtures/ap-*.json` (copied)
- Test: `windows/tests/Elevate.Core.Tests/AccessPackageProviderTests.cs`

**Interfaces:**
- Consumes: Task 1 scopes, Task 2 models, `GraphTransport` (`GraphUrl`, `ListAllAsync`, `PostAsync`, `Page<T>`), `GraphJson.Options`.
- Produces (namespace `Elevate.Core.Providers`):

```csharp
public interface IAccessPackageProvider
{
    IReadOnlyList<string> Scopes { get; }
    Task<IReadOnlyList<AccessPackage>> RequestablePackagesAsync(Identity identity, string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<AccessPackageRequest>> MyRequestsAsync(Identity identity, string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<AccessPackageAssignment>> MyAssignmentsAsync(Identity identity, string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PolicyRequirement>> RequirementsAsync(string packageId, Identity identity, string tenantId, CancellationToken ct = default);
    Task<AccessPackageRequest> RequestAsync(string packageId, string? policyId, string justification, Identity identity, string tenantId, CancellationToken ct = default);
    Task CancelAsync(string requestId, Identity identity, string tenantId, CancellationToken ct = default);
}
```
  and `sealed class AccessPackageProvider(IHttpClient http, ITokenProvider tokens) : IAccessPackageProvider` with `static Uri MyAccessUrl(string tenantId, string packageId)`.

- [ ] **Step 1: Copy the fixtures**

```bash
cd /Users/frode.hus/pimtray && cp macos/Tests/ElevateCoreTests/Fixtures/ap-*.json windows/tests/Elevate.Core.Tests/Fixtures/ && ls windows/tests/Elevate.Core.Tests/Fixtures/ap-*.json | wc -l
```

Expected: `8`.

- [ ] **Step 2: Write the failing tests**

Create `windows/tests/Elevate.Core.Tests/AccessPackageProviderTests.cs`:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AccessPackageProviderTests</c>.</summary>
public class AccessPackageProviderTests
{
    private static readonly Identity TestIdentity = new("id1", "alex.rivera@contoso.com", "Alex", "t1");

    private static (AccessPackageProvider Provider, StubHttpClient Http) MakeProvider()
    {
        var http = new StubHttpClient();
        return (new AccessPackageProvider(http, new FakeTokenProvider()), http);
    }

    [Fact]
    public async Task ListsRequestablePackagesAcrossPages()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "accessPackages/filterByCurrentUser", body: Fixtures.Data("ap-packages"));
        http.On("GET", "skiptoken=page2", body: Fixtures.Data("ap-packages-page2"));

        var packages = await p.RequestablePackagesAsync(TestIdentity, "t1");

        packages.Select(x => x.Id).Should().Equal("pkg-finance", "pkg-exchange", "pkg-sandbox", "pkg-hidden");
        packages[3].IsHidden.Should().BeTrue();
        packages[3].Description.Should().BeNull();
        var first = http.Requests[0];
        first.Headers["Authorization"].Should().Be("Bearer token-t1");
        first.Url.AbsoluteUri.Should().Contain("identityGovernance/entitlementManagement/accessPackages/filterByCurrentUser(on='allowedRequestor')");
    }

    [Fact]
    public async Task ListsMyRequestsWithPackageNamesAndStates()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "assignmentRequests/filterByCurrentUser", body: Fixtures.Data("ap-requests"));

        var requests = await p.MyRequestsAsync(TestIdentity, "t1");

        requests.Select(r => r.Id).Should().Equal("req-1", "req-2", "req-3", "req-4");
        requests[0].State.Should().Be(AccessPackageRequestState.PendingApproval);
        requests[0].PackageName.Should().Be("Azure Sandbox Contributor");
        requests[0].PackageId.Should().Be("pkg-sandbox");
        requests[0].PolicyId.Should().Be("pol-eng");
        requests[0].Justification.Should().Be("Need a sandbox for the cost-alerting spike (INC-4412).");
        requests[1].State.Should().Be(AccessPackageRequestState.Delivered);
        requests[1].CompletedAt.Should().Be(Fixtures.Date("2026-09-07T14:40:00Z"));
        requests[2].State.Should().Be(AccessPackageRequestState.Denied);
        // Unknown state and a missing package still decode; the name falls back to the id.
        requests[3].State.Should().Be(AccessPackageRequestState.Unknown);
        requests[3].PackageName.Should().Be("req-4");
        requests[3].PackageId.Should().BeEmpty();
        var url = http.Requests[0].Url.AbsoluteUri;
        url.Should().Contain("assignmentRequests/filterByCurrentUser(on='target')").And.Contain("expand=accessPackage,assignment");
    }

    [Fact]
    public async Task ListsMyAssignmentsWithExpiryAndPolicy()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "assignments/filterByCurrentUser", body: Fixtures.Data("ap-assignments"));

        var assignments = await p.MyAssignmentsAsync(TestIdentity, "t1");

        assignments.Select(a => a.Id).Should().Equal("asg-1", "asg-2", "asg-3");
        assignments[0].State.Should().Be(AccessPackageAssignmentState.Delivered);
        assignments[0].ExpiresAt.Should().Be(Fixtures.Date("2027-03-07T14:40:00Z"));
        assignments[0].PolicyName.Should().Be("On-call staff");
        assignments[1].ExpiresAt.Should().BeNull();
        assignments[2].State.Should().Be(AccessPackageAssignmentState.Expired);
        assignments[2].PolicyName.Should().BeNull();
        var url = http.Requests[0].Url.AbsoluteUri;
        url.Should().Contain("assignments/filterByCurrentUser(on='target')").And.Contain("expand=accessPackage,assignmentPolicy");
    }

    [Fact]
    public async Task ForbiddenMapsToConsentRequiredForOwnApp()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "accessPackages/filterByCurrentUser", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", status: 403);

        var act = () => p.RequestablePackagesAsync(TestIdentity, "t1");

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public async Task ForbiddenIsAPlainRefusalForAFirstPartyApp()
    {
        var (p, http) = MakeProvider();
        http.On("GET", "accessPackages/filterByCurrentUser", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", status: 403);

        var act = () => p.RequestablePackagesAsync(TestIdentity with { SignInMethod = SignInMethod.AzureCLI }, "t1");

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.Forbidden);
    }

    [Fact]
    public async Task RequirementsWithOnePolicy()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "getApplicablePolicyRequirements", body: Fixtures.Data("ap-requirements-one"));

        var reqs = await p.RequirementsAsync("pkg-sandbox", TestIdentity, "t1");

        reqs.Should().ContainSingle();
        reqs[0].Id.Should().Be("pol-eng");
        reqs[0].DisplayName.Should().Be("Engineers");
        reqs[0].Description.Should().Be("30 days, approval by the platform team.");
        reqs[0].IsApprovalRequired.Should().BeTrue();
        reqs[0].RequiresAnswers.Should().BeFalse();
        var sent = http.Requests[0];
        sent.Method.Should().Be("POST");
        sent.Url.AbsoluteUri.Should().EndWith("/entitlementManagement/accessPackages/pkg-sandbox/getApplicablePolicyRequirements");
    }

    [Fact]
    public async Task RequirementsWithTwoPoliciesAndQuestions()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "pkg-two/getApplicablePolicyRequirements", body: Fixtures.Data("ap-requirements-two"));
        http.On("POST", "pkg-q/getApplicablePolicyRequirements", body: Fixtures.Data("ap-requirements-questions"));

        var two = await p.RequirementsAsync("pkg-two", TestIdentity, "t1");
        two.Select(r => r.Id).Should().Equal("pol-eng", "pol-lead");
        two[1].IsApprovalRequired.Should().BeFalse();

        var q = await p.RequirementsAsync("pkg-q", TestIdentity, "t1");
        q.Should().ContainSingle().Which.RequiresAnswers.Should().BeTrue();
    }

    [Fact]
    public async Task RequestPostsUserAddBodyWithOptionalPolicy()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "assignmentRequests", status: 201, body: Fixtures.Data("ap-request-created"));

        var created = await p.RequestAsync("pkg-sandbox", "pol-eng", "Need it", TestIdentity, "t1");

        created.Id.Should().Be("req-new");
        created.State.Should().Be(AccessPackageRequestState.Submitted);
        created.PackageId.Should().Be("pkg-sandbox");
        created.PolicyId.Should().Be("pol-eng");
        var body = JsonNode.Parse(Encoding.UTF8.GetString(http.Requests[0].Body!))!.AsObject();
        body["requestType"]!.GetValue<string>().Should().Be("userAdd");
        body["justification"]!.GetValue<string>().Should().Be("Need it");
        var assignment = body["assignment"]!.AsObject();
        assignment["accessPackageId"]!.GetValue<string>().Should().Be("pkg-sandbox");
        assignment["assignmentPolicyId"]!.GetValue<string>().Should().Be("pol-eng");

        _ = await p.RequestAsync("pkg-sandbox", null, "Again", TestIdentity, "t1");
        var second = JsonNode.Parse(Encoding.UTF8.GetString(http.Requests[^1].Body!))!.AsObject();
        second["assignment"]!.AsObject().ContainsKey("assignmentPolicyId").Should().BeFalse();
    }

    [Fact]
    public async Task CancelPostsToTheCancelAction()
    {
        var (p, http) = MakeProvider();
        http.On("POST", "assignmentRequests/req-1/cancel", status: 204);

        await p.CancelAsync("req-1", TestIdentity, "t1");

        var sent = http.Requests[0];
        sent.Method.Should().Be("POST");
        sent.Url.AbsoluteUri.Should().EndWith("/assignmentRequests/req-1/cancel");
    }

    [Fact]
    public void MyAccessUrlPointsAtThePackageInTheTenant()
    {
        AccessPackageProvider.MyAccessUrl("11111111-2222-3333-4444-555555555555", "pkg-sandbox").ToString()
            .Should().Be("https://myaccess.microsoft.com/@11111111-2222-3333-4444-555555555555#/access-packages/pkg-sandbox");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AccessPackageProviderTests" 2>&1 | tail -15`
Expected: build errors (no `AccessPackageProvider`).

- [ ] **Step 4: Implement**

Create `windows/src/Elevate.Core/Providers/AccessPackageProvider.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;

namespace Elevate.Core.Providers;

/// <summary>Self-service entitlement management for the signed-in user. Port of the Swift <c>AccessPackageProvider</c>.</summary>
public interface IAccessPackageProvider
{
    IReadOnlyList<string> Scopes { get; }

    /// <summary>Every package the caller may request, hidden ones included but flagged.</summary>
    Task<IReadOnlyList<AccessPackage>> RequestablePackagesAsync(Identity identity, string tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<AccessPackageRequest>> MyRequestsAsync(Identity identity, string tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<AccessPackageAssignment>> MyAssignmentsAsync(Identity identity, string tenantId, CancellationToken ct = default);

    /// <summary>One entry per policy the caller may request <paramref name="packageId"/> under.</summary>
    Task<IReadOnlyList<PolicyRequirement>> RequirementsAsync(string packageId, Identity identity, string tenantId, CancellationToken ct = default);

    /// <summary>Submits a <c>userAdd</c> request. <paramref name="policyId"/> is required by Graph only when several policies apply.</summary>
    Task<AccessPackageRequest> RequestAsync(string packageId, string? policyId, string justification, Identity identity, string tenantId, CancellationToken ct = default);

    Task CancelAsync(string requestId, Identity identity, string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Self-service entitlement management for the signed-in user: the access packages they may
/// request, their own requests and assignments. Graph v1.0 only.
/// </summary>
public sealed class AccessPackageProvider : IAccessPackageProvider
{
    internal const string Base = "/identityGovernance/entitlementManagement";

    private readonly GraphTransport _transport;

    public AccessPackageProvider(IHttpClient http, ITokenProvider tokens)
        => _transport = new GraphTransport(http, tokens);

    public IReadOnlyList<string> Scopes => Auth.Scopes.EntitlementAll;

    // MARK: Wire shapes

    private sealed record Named(string? Id, string? DisplayName);

    private sealed record PackageDto(string Id, string? DisplayName, string? Description, bool? IsHidden);

    private sealed record AssignmentRef(string? Id, string? AccessPackageId, string? AssignmentPolicyId);

    private sealed record RequestDto(
        string Id,
        string? RequestType,
        string? State,
        string? Status,
        string? Justification,
        DateTimeOffset? CreatedDateTime,
        DateTimeOffset? CompletedDateTime,
        Named? AccessPackage,
        AssignmentRef? Assignment);

    private sealed record ExpirationDto(string? Type, DateTimeOffset? EndDateTime);

    private sealed record ScheduleDto(DateTimeOffset? StartDateTime, ExpirationDto? Expiration);

    private sealed record AssignmentDto(
        string Id,
        string? State,
        string? Status,
        ScheduleDto? Schedule,
        Named? AccessPackage,
        Named? AssignmentPolicy);

    private sealed record QuestionDto(string? Id, bool? IsRequired);

    private sealed record RequirementDto(
        string? PolicyId,
        string? PolicyDisplayName,
        string? PolicyDescription,
        bool? IsApprovalRequired,
        IReadOnlyList<QuestionDto>? Questions);

    private static AccessPackageRequest Request(RequestDto r) => new(
        r.Id,
        r.AccessPackage?.Id ?? r.Assignment?.AccessPackageId ?? string.Empty,
        r.AccessPackage?.DisplayName ?? r.Id,
        r.RequestType ?? "userAdd",
        AccessPackageRequestStates.Parse(r.State),
        r.Status,
        r.Justification,
        r.CreatedDateTime,
        r.CompletedDateTime,
        r.Assignment?.AssignmentPolicyId);

    private static AccessPackageAssignment Assignment(AssignmentDto a) => new(
        a.Id,
        a.AccessPackage?.Id ?? string.Empty,
        a.AccessPackage?.DisplayName ?? a.Id,
        AccessPackageAssignmentStates.Parse(a.State),
        a.AssignmentPolicy?.DisplayName,
        a.Schedule?.Expiration?.EndDateTime);

    // MARK: Reads

    public async Task<IReadOnlyList<AccessPackage>> RequestablePackagesAsync(Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl(Base + "/accessPackages/filterByCurrentUser(on='allowedRequestor')");
        var items = await _transport.ListAllAsync<PackageDto>(identity, tenantId, url, Scopes, ct).ConfigureAwait(false);
        return [.. items.Select(p => new AccessPackage(p.Id, p.DisplayName ?? p.Id, p.Description, p.IsHidden ?? false))];
    }

    public async Task<IReadOnlyList<AccessPackageRequest>> MyRequestsAsync(Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl(Base + "/assignmentRequests/filterByCurrentUser(on='target')?$expand=accessPackage,assignment");
        var items = await _transport.ListAllAsync<RequestDto>(identity, tenantId, url, Scopes, ct).ConfigureAwait(false);
        return [.. items.Select(Request)];
    }

    public async Task<IReadOnlyList<AccessPackageAssignment>> MyAssignmentsAsync(Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl(Base + "/assignments/filterByCurrentUser(on='target')?$expand=accessPackage,assignmentPolicy");
        var items = await _transport.ListAllAsync<AssignmentDto>(identity, tenantId, url, Scopes, ct).ConfigureAwait(false);
        return [.. items.Select(Assignment)];
    }

    // MARK: Requirements and requests

    public async Task<IReadOnlyList<PolicyRequirement>> RequirementsAsync(string packageId, Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentNullException.ThrowIfNull(identity);
        var url = _transport.GraphUrl($"{Base}/accessPackages/{packageId}/getApplicablePolicyRequirements");
        var response = await _transport.PostAsync(identity, tenantId, url, Scopes, [], ct).ConfigureAwait(false);
        var page = JsonSerializer.Deserialize<GraphTransport.Page<RequirementDto>>(response.Body, GraphJson.Options);
        return
        [
            .. (page?.Value ?? [])
                .Where(d => d.PolicyId is not null)
                .Select(d => new PolicyRequirement(
                    d.PolicyId!, d.PolicyDisplayName ?? d.PolicyId!, d.PolicyDescription,
                    d.IsApprovalRequired ?? false, (d.Questions?.Count ?? 0) > 0)),
        ];
    }

    public async Task<AccessPackageRequest> RequestAsync(string packageId, string? policyId, string justification, Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageId);
        ArgumentNullException.ThrowIfNull(justification);
        ArgumentNullException.ThrowIfNull(identity);
        var assignment = new JsonObject { ["accessPackageId"] = packageId };
        if (policyId is not null)
        {
            assignment["assignmentPolicyId"] = policyId;
        }

        var body = new JsonObject
        {
            ["requestType"] = "userAdd",
            ["justification"] = justification,
            ["assignment"] = assignment,
        };
        var response = await _transport.PostAsync(
            identity, tenantId, _transport.GraphUrl(Base + "/assignmentRequests"),
            Scopes, Encoding.UTF8.GetBytes(body.ToJsonString()), ct).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<RequestDto>(response.Body, GraphJson.Options)
            ?? throw new PimException(PimErrorKind.Unexpected, "Empty response body");
        var created = Request(dto);
        return created.PackageId.Length == 0 ? created with { PackageId = packageId } : created;
    }

    public async Task CancelAsync(string requestId, Identity identity, string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(identity);
        await _transport.PostAsync(
            identity, tenantId, _transport.GraphUrl($"{Base}/assignmentRequests/{requestId}/cancel"), Scopes, [], ct).ConfigureAwait(false);
    }

    /// <summary>The My Access portal page for one package, for policies whose questions Elevate does not collect.</summary>
    public static Uri MyAccessUrl(string tenantId, string packageId) =>
        new($"https://myaccess.microsoft.com/@{tenantId}#/access-packages/{packageId}");
}
```

- [ ] **Step 5: Run the tests**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AccessPackageProviderTests" 2>&1 | tail -8`
Expected: 10 passed. If `MyAccessUrlPointsAtThePackageInTheTenant` fails because `Uri.ToString()` rewrites the `@`, compare `OriginalString` instead in the test and note it in the commit message.

- [ ] **Step 6: Commit**

```bash
cd /Users/frode.hus/pimtray && git add windows/src/Elevate.Core/Providers/AccessPackageProvider.cs windows/tests/Elevate.Core.Tests/AccessPackageProviderTests.cs windows/tests/Elevate.Core.Tests/Fixtures/ap-*.json && git commit -q -m "Core: AccessPackageProvider over Graph v1.0 with recorded fixtures"
```

---

### Task 4: AccessPackageDiff

**Files:**
- Create: `windows/src/Elevate.Core/Coordination/AccessPackageDiff.cs`
- Test: `windows/tests/Elevate.Core.Tests/AccessPackageDiffTests.cs`

**Interfaces:**
- Produces (namespace `Elevate.Core.Coordination`): `abstract record AccessPackageEvent { string PackageName }` with nested `Approved(AccessPackageRequest Request)`, `Denied(AccessPackageRequest Request)`, `DeliveryFailed(AccessPackageRequest Request)`, `Revoked(AccessPackageAssignment Assignment)`, `Expired(AccessPackageAssignment Assignment)`; `static class AccessPackageDiff { static IReadOnlyList<AccessPackageEvent> Events(AccessPackageSnapshot? previous, AccessPackageSnapshot current, DateTimeOffset now) }`.

- [ ] **Step 1: Write the failing tests**

Create `windows/tests/Elevate.Core.Tests/AccessPackageDiffTests.cs`:

```csharp
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Tests.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>AccessPackageDiffTests</c>.</summary>
public class AccessPackageDiffTests
{
    private static readonly DateTimeOffset Now = Fixtures.Date("2026-09-08T12:00:00Z")!.Value;

    private static AccessPackageRequest Request(string id, AccessPackageRequestState state) =>
        new(id, $"p-{id}", $"Package {id}", "userAdd", state);

    private static AccessPackageAssignment Assignment(string id, AccessPackageAssignmentState state = AccessPackageAssignmentState.Delivered, string? expires = null) =>
        new(id, $"p-{id}", $"Package {id}", state, ExpiresAt: expires is null ? null : Fixtures.Date(expires));

    [Fact]
    public void NullPreviousIsBaselineAndYieldsNothing()
    {
        var current = new AccessPackageSnapshot([Request("r", AccessPackageRequestState.Delivered)], [Assignment("a")]);
        AccessPackageDiff.Events(null, current, Now).Should().BeEmpty();
    }

    [Fact]
    public void UnchangedYieldsNothing()
    {
        var s = new AccessPackageSnapshot([Request("r", AccessPackageRequestState.PendingApproval)], [Assignment("a")]);
        AccessPackageDiff.Events(s, s, Now).Should().BeEmpty();
    }

    [Fact]
    public void RequestTransitionsProduceEvents()
    {
        var before = new AccessPackageSnapshot([
            Request("a", AccessPackageRequestState.PendingApproval), Request("b", AccessPackageRequestState.Submitted),
            Request("c", AccessPackageRequestState.Delivering), Request("d", AccessPackageRequestState.PendingApproval)]);
        var after = new AccessPackageSnapshot([
            Request("a", AccessPackageRequestState.Delivered), Request("b", AccessPackageRequestState.Denied),
            Request("c", AccessPackageRequestState.DeliveryFailed), Request("d", AccessPackageRequestState.Canceled)]);

        var events = AccessPackageDiff.Events(before, after, Now);

        events.Should().Equal(
            new AccessPackageEvent.Approved(Request("a", AccessPackageRequestState.Delivered)),
            new AccessPackageEvent.Denied(Request("b", AccessPackageRequestState.Denied)),
            new AccessPackageEvent.DeliveryFailed(Request("c", AccessPackageRequestState.DeliveryFailed)));
    }

    [Fact]
    public void ARequestFirstSeenAlreadyDeliveredDoesNotNotify()
    {
        var before = new AccessPackageSnapshot();
        var after = new AccessPackageSnapshot([Request("a", AccessPackageRequestState.Delivered)]);
        AccessPackageDiff.Events(before, after, Now).Should().BeEmpty();
    }

    [Fact]
    public void RevokedBeforeExpiryAndExpiredAtExpiry()
    {
        var before = new AccessPackageSnapshot(assignments: [
            Assignment("keep", expires: "2027-01-01T00:00:00Z"),
            Assignment("gone-early", expires: "2027-01-01T00:00:00Z"),
            Assignment("gone-late", expires: "2026-09-08T11:00:00Z"),
            Assignment("marked", expires: "2026-09-01T00:00:00Z"),
            Assignment("open-ended")]);
        var after = new AccessPackageSnapshot(assignments: [
            Assignment("keep", expires: "2027-01-01T00:00:00Z"),
            Assignment("marked", AccessPackageAssignmentState.Expired, "2026-09-01T00:00:00Z")]);

        var events = AccessPackageDiff.Events(before, after, Now);

        events.Should().HaveCount(4);
        events.Should().Contain(new AccessPackageEvent.Revoked(before.Assignments[1]));
        events.Should().Contain(new AccessPackageEvent.Expired(before.Assignments[2]));
        events.Should().Contain(new AccessPackageEvent.Expired(before.Assignments[3]));
        events.Should().Contain(new AccessPackageEvent.Revoked(before.Assignments[4]));
    }

    [Fact]
    public void EventsCarryTheirPackageName()
    {
        new AccessPackageEvent.Approved(Request("x", AccessPackageRequestState.Delivered)).PackageName.Should().Be("Package x");
        new AccessPackageEvent.Expired(Assignment("y")).PackageName.Should().Be("Package y");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AccessPackageDiffTests" 2>&1 | tail -15`
Expected: build errors (no `AccessPackageDiff`).

- [ ] **Step 3: Implement**

Create `windows/src/Elevate.Core/Coordination/AccessPackageDiff.cs`:

```csharp
using Elevate.Core.Models;

namespace Elevate.Core.Coordination;

/// <summary>Something worth telling the user about between two polls of a tenant's access packages.</summary>
public abstract record AccessPackageEvent
{
    private AccessPackageEvent()
    {
    }

    public abstract string PackageName { get; }

    public sealed record Approved(AccessPackageRequest Request) : AccessPackageEvent
    {
        public override string PackageName => Request.PackageName;
    }

    public sealed record Denied(AccessPackageRequest Request) : AccessPackageEvent
    {
        public override string PackageName => Request.PackageName;
    }

    public sealed record DeliveryFailed(AccessPackageRequest Request) : AccessPackageEvent
    {
        public override string PackageName => Request.PackageName;
    }

    public sealed record Revoked(AccessPackageAssignment Assignment) : AccessPackageEvent
    {
        public override string PackageName => Assignment.PackageName;
    }

    public sealed record Expired(AccessPackageAssignment Assignment) : AccessPackageEvent
    {
        public override string PackageName => Assignment.PackageName;
    }
}

/// <summary>
/// Pure comparison of two snapshots. A null <c>previous</c> is the first sight of a tenant and
/// never notifies: everything present then is baseline, not news. Port of the Swift <c>AccessPackageDiff</c>.
/// </summary>
public static class AccessPackageDiff
{
    public static IReadOnlyList<AccessPackageEvent> Events(AccessPackageSnapshot? previous, AccessPackageSnapshot current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return [];
        }

        var events = new List<AccessPackageEvent>();

        // Requests: only a request we already knew in an open state can transition into news.
        var previousStates = new Dictionary<string, AccessPackageRequestState>(StringComparer.Ordinal);
        foreach (var r in previous.Requests)
        {
            previousStates.TryAdd(r.Id, r.State);
        }

        foreach (var r in current.Requests)
        {
            if (!previousStates.TryGetValue(r.Id, out var was) || !was.IsOpen() || was == r.State)
            {
                continue;
            }

            switch (r.State)
            {
                case AccessPackageRequestState.Delivered:
                    events.Add(new AccessPackageEvent.Approved(r));
                    break;
                case AccessPackageRequestState.Denied:
                    events.Add(new AccessPackageEvent.Denied(r));
                    break;
                case AccessPackageRequestState.DeliveryFailed:
                    events.Add(new AccessPackageEvent.DeliveryFailed(r));
                    break;
                default:
                    break;
            }
        }

        // Assignments: a delivered one that is gone or expired is either revoked or expired,
        // decided by whether its end date has passed.
        var currentById = new Dictionary<string, AccessPackageAssignment>(StringComparer.Ordinal);
        foreach (var a in current.Assignments)
        {
            currentById.TryAdd(a.Id, a);
        }

        foreach (var a in previous.Assignments.Where(a => a.State == AccessPackageAssignmentState.Delivered))
        {
            if (currentById.TryGetValue(a.Id, out var still) && still.State == AccessPackageAssignmentState.Delivered)
            {
                continue;
            }

            events.Add(a.ExpiresAt is { } end && end <= now
                ? new AccessPackageEvent.Expired(a)
                : new AccessPackageEvent.Revoked(a));
        }

        return events;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AccessPackageDiffTests" 2>&1 | tail -8`
Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add windows/src/Elevate.Core/Coordination/AccessPackageDiff.cs windows/tests/Elevate.Core.Tests/AccessPackageDiffTests.cs && git commit -q -m "Core: AccessPackageDiff events between two polls"
```

---

### Task 5: NewRoleTracker

**Files:**
- Create: `windows/src/Elevate.Core/Coordination/NewRoleTracker.cs`
- Test: `windows/tests/Elevate.Core.Tests/NewRoleTrackerTests.cs`

**Interfaces:**
- Produces (namespace `Elevate.Core.Coordination`): `sealed class NewRoleTracker : IEquatable<NewRoleTracker>` with `HashSet<RoleKey> Seen`, `HashSet<RoleKey> New`, `int ShownOpens`, `IReadOnlyList<RoleKey> Observe(IReadOnlySet<RoleKey> discovered)`, `void PanelOpened()`, `bool IsNew(RoleKey key)`, `NewRoleTracker Clone()`. JSON keys `seen`, `new`, `shownOpens`.

- [ ] **Step 1: Write the failing tests**

Create `windows/tests/Elevate.Core.Tests/NewRoleTrackerTests.cs`:

```csharp
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests;

/// <summary>Port of the Swift <c>NewRoleTrackerTests</c>.</summary>
public class NewRoleTrackerTests
{
    private static RoleKey Key(string n) => new("i", "t", new EntraDirectoryScope(n, "/"));

    private static HashSet<RoleKey> Keys(params string[] names) => [.. names.Select(Key)];

    [Fact]
    public void FirstObserveBaselinesWithoutReportingAdditions()
    {
        var t = new NewRoleTracker();
        var added = t.Observe(Keys("a", "b"));
        added.Should().BeEmpty();
        t.Seen.Should().BeEquivalentTo(Keys("a", "b"));
        t.New.Should().BeEmpty();
    }

    [Fact]
    public void AdditionsAreReportedOnceAndMarkedNew()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        var added = t.Observe(Keys("a", "c", "b"));
        added.Should().BeEquivalentTo(Keys("b", "c"));
        t.IsNew(Key("b")).Should().BeTrue();
        t.IsNew(Key("c")).Should().BeTrue();
        t.IsNew(Key("a")).Should().BeFalse();
        t.Observe(Keys("a", "b", "c")).Should().BeEmpty();
        t.New.Should().BeEquivalentTo(Keys("b", "c"));
    }

    [Fact]
    public void RemovalsAreIgnoredAndAReturningRoleIsNewAgain()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a", "b"));
        t.Observe(Keys("a")).Should().BeEmpty();
        t.Seen.Should().BeEquivalentTo(Keys("a"));
        t.Observe(Keys("a", "b")).Should().Equal(Key("b"));
    }

    [Fact]
    public void MarkerClearsOnTheSecondPanelOpen()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.Observe(Keys("a", "b"));
        t.PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
        t.PanelOpened();
        t.IsNew(Key("b")).Should().BeFalse();
        t.ShownOpens.Should().Be(0);
    }

    [Fact]
    public void PanelOpensWithNothingNewDoNotCount()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.PanelOpened();
        t.PanelOpened();
        t.PanelOpened();
        t.Observe(Keys("a", "b"));
        t.PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
    }

    [Fact]
    public void EmptyDiscoveryNeverBaselines()
    {
        var t = new NewRoleTracker();
        t.Observe(new HashSet<RoleKey>()).Should().BeEmpty();
        t.Seen.Should().BeEmpty();
        t.Observe(Keys("a")).Should().BeEmpty(); // still the first real sight
    }

    [Fact]
    public void RoundTripsThroughJsonWithSwiftKeys()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.Observe(Keys("a", "b"));
        t.PanelOpened();

        var json = Json.Serialize(t);
        json.Should().Contain("\"seen\":[").And.Contain("\"new\":[").And.Contain("\"shownOpens\":1");
        Json.Deserialize<NewRoleTracker>(json).Should().Be(t);
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var t = new NewRoleTracker();
        t.Observe(Keys("a"));
        t.Observe(Keys("a", "b"));
        var copy = t.Clone();
        copy.Should().Be(t);
        copy.PanelOpened();
        copy.PanelOpened();
        t.IsNew(Key("b")).Should().BeTrue();
        copy.IsNew(Key("b")).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~NewRoleTrackerTests" 2>&1 | tail -15`
Expected: build errors (no `NewRoleTracker`).

- [ ] **Step 3: Implement**

Create `windows/src/Elevate.Core/Coordination/NewRoleTracker.cs`:

```csharp
using Elevate.Core.Models;

namespace Elevate.Core.Coordination;

/// <summary>
/// Remembers which eligible roles a tenant has shown before, so a panel can mark additions as
/// new and an app can notify about them. Pure state: the model calls <see cref="Observe"/> after
/// each discovery and <see cref="PanelOpened"/> each time the panel opens. Port of the Swift
/// <c>NewRoleTracker</c>; the JSON shape (<c>seen</c>, <c>new</c>, <c>shownOpens</c>) is shared.
/// </summary>
public sealed class NewRoleTracker : IEquatable<NewRoleTracker>
{
    private HashSet<RoleKey> _seen = [];
    private HashSet<RoleKey> _new = [];

    /// <summary>Every role key seen in the most recent discovery. Empty means "never baselined".</summary>
    public HashSet<RoleKey> Seen
    {
        get => _seen;
        set => _seen = value ?? [];
    }

    /// <summary>Roles added since the baseline that the panel still marks.</summary>
    public HashSet<RoleKey> New
    {
        get => _new;
        set => _new = value ?? [];
    }

    /// <summary>Panel opens since <see cref="New"/> became non-empty; the marker clears at two.</summary>
    public int ShownOpens { get; set; }

    /// <summary>
    /// Records a discovery. Returns the additions, in no particular order. An empty discovery is
    /// ignored (a failed or consent-blocked read must not baseline away real roles), and the
    /// first non-empty discovery only baselines.
    /// </summary>
    public IReadOnlyList<RoleKey> Observe(IReadOnlySet<RoleKey> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        if (discovered.Count == 0)
        {
            return [];
        }

        try
        {
            if (Seen.Count == 0)
            {
                return [];
            }

            var added = discovered.Where(k => !Seen.Contains(k)).ToList();
            New.UnionWith(added);
            // A role that disappeared stops being "new"; it becomes new again if it returns.
            New.IntersectWith(discovered);
            return added;
        }
        finally
        {
            Seen = [.. discovered];
        }
    }

    /// <summary>Counts a panel open while something is marked; the second one clears the marker.</summary>
    public void PanelOpened()
    {
        if (New.Count == 0)
        {
            return;
        }

        ShownOpens += 1;
        if (ShownOpens >= 2)
        {
            New.Clear();
            ShownOpens = 0;
        }
    }

    public bool IsNew(RoleKey key) => New.Contains(key);

    public NewRoleTracker Clone() => new() { Seen = [.. Seen], New = [.. New], ShownOpens = ShownOpens };

    public bool Equals(NewRoleTracker? other) =>
        other is not null && ShownOpens == other.ShownOpens && Seen.SetEquals(other.Seen) && New.SetEquals(other.New);

    public override bool Equals(object? obj) => Equals(obj as NewRoleTracker);

    public override int GetHashCode() => HashCode.Combine(Seen.Count, New.Count, ShownOpens);
}
```

- [ ] **Step 4: Run the tests**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~NewRoleTrackerTests" 2>&1 | tail -8`
Expected: 8 passed.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add windows/src/Elevate.Core/Coordination/NewRoleTracker.cs windows/tests/Elevate.Core.Tests/NewRoleTrackerTests.cs && git commit -q -m "Core: NewRoleTracker for the new-role marker"
```

---

### Task 6: AppState records and state.json interop

**Files:**
- Modify: `windows/src/Elevate.Core/Storage/AppState.cs`
- Modify: `windows/tests/Elevate.Core.Tests/Fixtures/state-macos.json`
- Test: `windows/tests/Elevate.Core.Tests/AppStateStoreTests.cs`, `windows/tests/Elevate.Core.Tests/AppStateGoldenTests.cs`

**Interfaces:**
- Consumes: `AccessPackageSnapshot` (Task 2), `NewRoleTracker` (Task 5).
- Produces (namespace `Elevate.Core.Storage`): `sealed record AccessPackageRecord(TenantKey TenantKey, AccessPackageSnapshot Snapshot, DateTimeOffset PolledAt)`, `sealed record RoleTrackingRecord(TenantKey TenantKey, NewRoleTracker Tracker)`; on `AppState`: `List<AccessPackageRecord> AccessPackages`, `List<RoleTrackingRecord> RoleTracking`, `AccessPackageRecord? AccessPackagesFor(TenantKey key)`, `void SetAccessPackages(TenantKey key, AccessPackageSnapshot snapshot, DateTimeOffset polledAt)`, `NewRoleTracker RoleTrackerFor(TenantKey key)` (a fresh tracker when none is stored), `void SetRoleTracker(TenantKey key, NewRoleTracker tracker)`.

- [ ] **Step 1: Write the failing tests**

Append to `AppStateStoreTests.cs` inside the class:

```csharp
    [Fact]
    public void AccessPackageRecordsRoundTripAndDefaultEmpty()
    {
        var store = new AppStateStore(TempDir());
        var state = new AppState();
        var key = new TenantKey("i", "t");
        var snapshot = new AccessPackageSnapshot([new AccessPackageRequest("r", "p", "Pkg", "userAdd", AccessPackageRequestState.PendingApproval)]);
        var polledAt = Fixtures.Date("2026-09-08T07:00:00Z")!.Value;
        state.SetAccessPackages(key, snapshot, polledAt);
        var tracker = new NewRoleTracker();
        tracker.Observe(new HashSet<RoleKey> { Key });
        state.SetRoleTracker(key, tracker);

        store.Save(state);
        var loaded = store.Load();

        loaded.AccessPackagesFor(key)!.Snapshot.Should().Be(snapshot);
        loaded.AccessPackagesFor(key)!.PolledAt.Should().Be(polledAt);
        loaded.RoleTrackerFor(key).Should().Be(tracker);
        loaded.RoleTrackerFor(new TenantKey("i", "other")).Should().Be(new NewRoleTracker());
        loaded.Should().Be(state);
    }

    [Fact]
    public void SetAccessPackagesAndSetRoleTrackerReplacePerTenant()
    {
        var state = new AppState();
        var key = new TenantKey("i", "t");
        state.SetAccessPackages(key, new AccessPackageSnapshot(), Fixtures.Date("2026-09-08T07:00:00Z")!.Value);
        state.SetAccessPackages(key, new AccessPackageSnapshot(), Fixtures.Date("2026-09-08T08:00:00Z")!.Value);
        state.SetRoleTracker(key, new NewRoleTracker());
        state.SetRoleTracker(key, new NewRoleTracker { ShownOpens = 1 });

        state.AccessPackages.Should().ContainSingle().Which.PolledAt.Should().Be(Fixtures.Date("2026-09-08T08:00:00Z")!.Value);
        state.RoleTracking.Should().ContainSingle().Which.Tracker.ShownOpens.Should().Be(1);
    }

    [Fact]
    public void LegacyStateWithoutAccessPackagesLoads()
    {
        var state = Json.Deserialize<AppState>("""{"identities":[],"tenants":[],"manualRoles":[],"memory":[],"profiles":[]}""")!;
        state.AccessPackages.Should().BeEmpty();
        state.RoleTracking.Should().BeEmpty();
        Json.Deserialize<AppState>("""{"accessPackages":null,"roleTracking":null}""").Should().Be(new AppState());
    }

    [Fact]
    public void RemovingATenantDropsItsAccessPackageState()
    {
        var state = new AppState();
        var key = new TenantKey("i", "t");
        state.SetAccessPackages(key, new AccessPackageSnapshot(), DateTimeOffset.UtcNow);
        state.SetRoleTracker(key, new NewRoleTracker());

        state.RemoveTenant(key);

        state.AccessPackagesFor(key).Should().BeNull();
        state.RoleTracking.Should().BeEmpty();
    }

    [Fact]
    public void CloneCopiesTrackersDeeply()
    {
        var state = new AppState();
        var key = new TenantKey("i", "t");
        var tracker = new NewRoleTracker();
        tracker.Observe(new HashSet<RoleKey> { Key });
        tracker.Observe(new HashSet<RoleKey> { Key, new RoleKey("i", "t", new EntraDirectoryScope("r2", "/")) });
        state.SetRoleTracker(key, tracker);
        state.SetAccessPackages(key, new AccessPackageSnapshot(), DateTimeOffset.UtcNow);

        var clone = state.Clone();
        clone.Should().Be(state);
        clone.RoleTrackerFor(key).PanelOpened();
        clone.RoleTrackerFor(key).PanelOpened();
        clone.AccessPackages.Clear();

        state.RoleTrackerFor(key).New.Should().ContainSingle();
        state.AccessPackages.Should().ContainSingle();
    }
```

Add `using Elevate.Core.Coordination;` and `using Elevate.Core.Tests.Support;` at the top of `AppStateStoreTests.cs`.

Extend `Fixtures/state-macos.json`: add `"accessPackagesAvailable" : true` to the `t-home` tenant object (after `"principalObjectId"`), and add two top-level keys after `"profiles"` (keep the trailing structure valid JSON):

```json
  "accessPackages" : [
    {
      "tenantKey" : {
        "identityId" : "id1",
        "tenantId" : "t-home"
      },
      "snapshot" : {
        "requests" : [
          {
            "id" : "req-1",
            "packageId" : "pkg-sandbox",
            "packageName" : "Azure Sandbox Contributor",
            "requestType" : "userAdd",
            "state" : "pendingApproval",
            "status" : "PendingApproval",
            "justification" : "Need a sandbox for the cost-alerting spike (INC-4412).",
            "createdAt" : "2026-09-08T07:12:00Z",
            "policyId" : "pol-eng"
          }
        ],
        "assignments" : [
          {
            "id" : "asg-1",
            "packageId" : "pkg-exchange",
            "packageName" : "Exchange Operations",
            "state" : "delivered",
            "policyName" : "On-call staff",
            "expiresAt" : "2027-03-07T14:40:00Z"
          }
        ]
      },
      "polledAt" : "2026-09-08T07:15:00Z"
    }
  ],
  "roleTracking" : [
    {
      "tenantKey" : {
        "identityId" : "id1",
        "tenantId" : "t-home"
      },
      "tracker" : {
        "seen" : [
          {
            "identityId" : "id1",
            "tenantId" : "t-home",
            "scope" : {
              "entraDirectory" : {
                "roleDefinitionId" : "62e90394-69f5-4237-9190-012177145e10",
                "directoryScopeId" : "/"
              }
            }
          }
        ],
        "new" : [],
        "shownOpens" : 0
      }
    }
  ]
```

Append to `AppStateGoldenTests.MacOsStateFileDecodesToTheExpectedValues` at the end of the method:

```csharp
        state.Tenants[0].AccessPackagesAvailable.Should().BeTrue();
        state.Tenants[1].AccessPackagesAvailable.Should().BeNull();
        var home = new TenantKey("id1", "t-home");
        var record = state.AccessPackagesFor(home)!;
        record.PolledAt.Should().Be(Fixtures.Date("2026-09-08T07:15:00Z"));
        record.Snapshot.Requests.Should().ContainSingle().Which.State.Should().Be(AccessPackageRequestState.PendingApproval);
        record.Snapshot.Assignments.Should().ContainSingle().Which.ExpiresAt.Should().Be(Fixtures.Date("2027-03-07T14:40:00Z"));
        state.RoleTrackerFor(home).Seen.Should().ContainSingle().Which.Should().Be(entra);
        state.RoleTrackerFor(home).New.Should().BeEmpty();
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln --filter "FullyQualifiedName~AppStateStoreTests|FullyQualifiedName~AppStateGoldenTests" 2>&1 | tail -15`
Expected: build errors (no `SetAccessPackages` etc.).

- [ ] **Step 3: Implement**

In `Storage/AppState.cs`, add `using Elevate.Core.Coordination;` and, after `RoleMemory`:

```csharp
/// <summary>The last poll of one tenant's access packages, kept so the next poll can diff against it.</summary>
public sealed record AccessPackageRecord(TenantKey TenantKey, AccessPackageSnapshot Snapshot, DateTimeOffset PolledAt);

/// <summary>
/// Per-tenant new-role bookkeeping. An array of records, not a dictionary keyed by a struct, so
/// the macOS app can read the same file.
/// </summary>
public sealed record RoleTrackingRecord(TenantKey TenantKey, NewRoleTracker Tracker);
```

Inside `AppState`: two more backing fields and properties, mirroring `Profiles`:

```csharp
    private List<AccessPackageRecord> _accessPackages = [];
    private List<RoleTrackingRecord> _roleTracking = [];

    public List<AccessPackageRecord> AccessPackages
    {
        get => _accessPackages;
        set => _accessPackages = value ?? [];
    }

    public List<RoleTrackingRecord> RoleTracking
    {
        get => _roleTracking;
        set => _roleTracking = value ?? [];
    }
```

Helpers, after `Remember`:

```csharp
    public AccessPackageRecord? AccessPackagesFor(TenantKey key) => AccessPackages.Find(r => r.TenantKey == key);

    public void SetAccessPackages(TenantKey key, AccessPackageSnapshot snapshot, DateTimeOffset polledAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var record = new AccessPackageRecord(key, snapshot, polledAt);
        var index = AccessPackages.FindIndex(r => r.TenantKey == key);
        if (index >= 0)
        {
            AccessPackages[index] = record;
        }
        else
        {
            AccessPackages.Add(record);
        }
    }

    /// <summary>The stored tracker for a tenant, or a fresh one that is not yet stored.</summary>
    public NewRoleTracker RoleTrackerFor(TenantKey key) => RoleTracking.Find(r => r.TenantKey == key)?.Tracker ?? new NewRoleTracker();

    public void SetRoleTracker(TenantKey key, NewRoleTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        var record = new RoleTrackingRecord(key, tracker);
        var index = RoleTracking.FindIndex(r => r.TenantKey == key);
        if (index >= 0)
        {
            RoleTracking[index] = record;
        }
        else
        {
            RoleTracking.Add(record);
        }
    }
```

In `RemoveTenant`, after the profiles loop:

```csharp
        AccessPackages.RemoveAll(r => r.TenantKey == key);
        RoleTracking.RemoveAll(r => r.TenantKey == key);
```

In `Clone()`:

```csharp
        AccessPackages = [.. AccessPackages],
        RoleTracking = [.. RoleTracking.Select(r => r with { Tracker = r.Tracker.Clone() })],
```

In `Equals`: `&& AccessPackages.SequenceEqual(other.AccessPackages) && RoleTracking.SequenceEqual(other.RoleTracking)`. In `GetHashCode`: add `AccessPackages.Count, RoleTracking.Count` (HashCode.Combine takes up to eight arguments).

Update the class summary comment to mention access package snapshots and role tracking.

- [ ] **Step 4: Run the whole Core suite**

Run: `cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln 2>&1 | tail -5`
Expected: `Passed!`, no failures. `MacOsStateFileRoundTripsLosslessly` must still pass: the canonical comparison sorts keys, and the fixture's sets have one element so array order is stable.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add windows/src/Elevate.Core/Storage/AppState.cs windows/tests/Elevate.Core.Tests && git commit -q -m "Core: persist access package snapshots and role tracking per tenant"
```

---

### Task 7: CLI session methods for access packages

**Files:**
- Create: `cli/src/Elevate.Cli/Session/ElevateSession.AccessPackages.cs`
- Modify: `cli/src/Elevate.Cli/Session/ElevateSession.cs`
- Modify: `cli/tests/Elevate.Cli.Tests/Support/Fakes.cs`, `cli/tests/Elevate.Cli.Tests/Support/TestSession.cs`
- Test: `cli/tests/Elevate.Cli.Tests/PackageSessionTests.cs`

**Interfaces:**
- Consumes: `IAccessPackageProvider`, `AccessPackageProvider` (Task 3), `Scopes.EntitlementAll`, `AccessTokenClaims.PermitsEntitlementSelfService` (Task 1), the session's existing `Acquire`, `Upsert`, `Identity`, `Tenant`, `Tokens`, `Persist`.
- Produces on `ElevateSession`:
  - `IAccessPackageProvider Packages { get; }`, constructor parameter `IAccessPackageProvider? accessPackages = null` (defaults to `new AccessPackageProvider(http, tokens)`).
  - `sealed record TenantPackages(TenantKey Key, IReadOnlyList<AccessPackage> Packages, IReadOnlyList<AccessPackageRequest> Requests, IReadOnlyList<AccessPackageAssignment> Assignments)` (namespace `Elevate.Cli.Session`).
  - `string? AccessPackagesUnavailableReason(TenantKey key)`: non-null for first-party sign-in methods (Azure CLI / Azure PowerShell) and for a tenant already marked `AccessPackagesAvailable == false`.
  - `IReadOnlyList<TenantKey> AccessPackageTenants(string? account, string? tenant)`: tracked tenants matching the filters whose reason is null.
  - `Task<TenantPackages> ReadPackagesAsync(TenantKey key, bool includePackages, CancellationToken ct)`: reads requests and assignments (and the requestable packages when asked) through the interactive retry; on success marks the tenant `AccessPackagesAvailable = true` when it was not; on `ConsentRequired`/`Forbidden` marks it `false` and rethrows.
  - `Task<IReadOnlyList<PolicyRequirement>> PackageRequirementsAsync(TenantKey key, string packageId, CancellationToken ct)`.
  - `Task<AccessPackageRequest> RequestPackageAsync(TenantKey key, string packageId, string? policyId, string justification, CancellationToken ct)`.
  - `Task CancelPackageRequestAsync(TenantKey key, string requestId, CancellationToken ct)`.
  - `Task<bool?> ProbeAccessPackagesAsync(Identity identity, string tenantId)`: silent token read with `Scopes.EntitlementAll`, `false` for first-party methods, null when nothing can be told (mirrors the Swift `probeAccessPackages`).

- [ ] **Step 1: Write the fake and the failing tests**

Append to `cli/tests/Elevate.Cli.Tests/Support/Fakes.cs`:

```csharp
/// <summary>An access package provider whose lists and failures the test scripts.</summary>
public sealed class FakeAccessPackageProvider : IAccessPackageProvider
{
    public IReadOnlyList<string> Scopes { get; } = ["scope"];

    public List<AccessPackage> Packages { get; } = [];

    public List<AccessPackageRequest> Requests { get; } = [];

    public List<AccessPackageAssignment> Assignments { get; } = [];

    public Dictionary<string, List<PolicyRequirement>> Requirements { get; } = new(StringComparer.Ordinal);

    public List<(string PackageId, string? PolicyId, string Justification)> Requested { get; } = [];

    public List<string> Cancelled { get; } = [];

    /// <summary>Thrown by every read while set.</summary>
    public PimException? ReadError { get; set; }

    public Task<IReadOnlyList<AccessPackage>> RequestablePackagesAsync(Identity identity, string tenantId, CancellationToken ct = default) =>
        ReadError is { } e ? Task.FromException<IReadOnlyList<AccessPackage>>(e) : Task.FromResult<IReadOnlyList<AccessPackage>>([.. Packages]);

    public Task<IReadOnlyList<AccessPackageRequest>> MyRequestsAsync(Identity identity, string tenantId, CancellationToken ct = default) =>
        ReadError is { } e ? Task.FromException<IReadOnlyList<AccessPackageRequest>>(e) : Task.FromResult<IReadOnlyList<AccessPackageRequest>>([.. Requests]);

    public Task<IReadOnlyList<AccessPackageAssignment>> MyAssignmentsAsync(Identity identity, string tenantId, CancellationToken ct = default) =>
        ReadError is { } e ? Task.FromException<IReadOnlyList<AccessPackageAssignment>>(e) : Task.FromResult<IReadOnlyList<AccessPackageAssignment>>([.. Assignments]);

    public Task<IReadOnlyList<PolicyRequirement>> RequirementsAsync(string packageId, Identity identity, string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PolicyRequirement>>(Requirements.TryGetValue(packageId, out var list) ? [.. list] : []);

    public Task<AccessPackageRequest> RequestAsync(string packageId, string? policyId, string justification, Identity identity, string tenantId, CancellationToken ct = default)
    {
        Requested.Add((packageId, policyId, justification));
        var created = new AccessPackageRequest("req-new", packageId, Packages.FirstOrDefault(p => p.Id == packageId)?.DisplayName ?? packageId,
            "userAdd", AccessPackageRequestState.Submitted, "Accepted", justification, DateTimeOffset.UtcNow, null, policyId);
        Requests.Add(created);
        return Task.FromResult(created);
    }

    public Task CancelAsync(string requestId, Identity identity, string tenantId, CancellationToken ct = default)
    {
        Cancelled.Add(requestId);
        Requests.RemoveAll(r => r.Id == requestId);
        return Task.CompletedTask;
    }
}
```

In `TestSession.cs`: add `public FakeAccessPackageProvider Packages { get; }`, construct it (`Packages = new FakeAccessPackageProvider();`) before the session, and pass it as the last constructor argument: `new ElevateSession(Store, Settings, Tokens, new NoHttpClient(), [Entra, Azure, Groups], [EntraApprovals], Packages)`.

Create `cli/tests/Elevate.Cli.Tests/PackageSessionTests.cs`:

```csharp
using Elevate.Cli.Tests.Support;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using FluentAssertions;

namespace Elevate.Cli.Tests;

public class PackageSessionTests
{
    private static readonly TenantKey Home = TestSession.Tenant.Key;

    [Fact]
    public async Task ReadMarksTheTenantAvailableAndReturnsAllThreeLists()
    {
        using var t = new TestSession();
        t.Packages.Packages.Add(new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor", "30 days."));
        t.Packages.Requests.Add(new AccessPackageRequest("req-1", "pkg-sandbox", "Azure Sandbox Contributor", "userAdd", AccessPackageRequestState.PendingApproval));
        t.Packages.Assignments.Add(new AccessPackageAssignment("asg-1", "pkg-exchange", "Exchange Operations", AccessPackageAssignmentState.Delivered, "On-call staff"));

        var read = await t.Session.ReadPackagesAsync(Home, includePackages: true, CancellationToken.None);

        read.Packages.Should().ContainSingle();
        read.Requests.Should().ContainSingle();
        read.Assignments.Should().ContainSingle();
        t.Session.Tenant(Home)!.AccessPackagesAvailable.Should().BeTrue();
        new AppStateStore(t.Directory).Load().Tenants.Single().AccessPackagesAvailable.Should().BeTrue("the flag is persisted");
    }

    [Fact]
    public async Task ReadWithoutPackagesSkipsThePackageList()
    {
        using var t = new TestSession();
        t.Packages.Packages.Add(new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor"));

        var read = await t.Session.ReadPackagesAsync(Home, includePackages: false, CancellationToken.None);

        read.Packages.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsentRefusalMarksTheTenantUnavailableAndThrows()
    {
        using var t = new TestSession();
        t.Packages.ReadError = new PimException(PimErrorKind.ConsentRequired);

        var act = () => t.Session.ReadPackagesAsync(Home, includePackages: true, CancellationToken.None);

        await act.Should().ThrowAsync<PimException>();
        t.Session.Tenant(Home)!.AccessPackagesAvailable.Should().BeFalse();
        t.Session.AccessPackagesUnavailableReason(Home).Should().NotBeNull();
        t.Session.AccessPackageTenants(null, null).Should().BeEmpty();
    }

    [Fact]
    public void FirstPartyAccountsAreNeverEligible()
    {
        using var t = new TestSession();
        t.Session.State.Identities[0] = TestSession.Account with { SignInMethod = SignInMethod.AzureCLI };

        t.Session.AccessPackagesUnavailableReason(Home).Should().Contain("Azure CLI");
        t.Session.AccessPackageTenants(null, null).Should().BeEmpty();
    }

    [Fact]
    public void TenantFiltersNarrowTheCandidates()
    {
        using var t = new TestSession();
        t.Session.State.UpsertTenant(new TenantContext("id1", "t2", "Fabrikam", TenantSource.Manual));

        t.Session.AccessPackageTenants(null, null).Select(k => k.TenantId).Should().Equal("t1", "t2");
        t.Session.AccessPackageTenants(null, "fabrikam").Select(k => k.TenantId).Should().Equal("t2");
        t.Session.AccessPackageTenants("alex", "t1").Select(k => k.TenantId).Should().Equal("t1");
        t.Session.AccessPackageTenants("nobody", null).Should().BeEmpty();
    }

    [Fact]
    public async Task RequestAndCancelGoThroughTheProvider()
    {
        using var t = new TestSession();
        t.Packages.Packages.Add(new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor"));
        t.Packages.Requirements["pkg-sandbox"] = [new PolicyRequirement("pol-eng", "Engineers", null, true, false)];

        var requirements = await t.Session.PackageRequirementsAsync(Home, "pkg-sandbox", CancellationToken.None);
        requirements.Should().ContainSingle().Which.Id.Should().Be("pol-eng");

        var created = await t.Session.RequestPackageAsync(Home, "pkg-sandbox", "pol-eng", "Need it", CancellationToken.None);
        created.State.Should().Be(AccessPackageRequestState.Submitted);
        t.Packages.Requested.Should().Equal(("pkg-sandbox", "pol-eng", "Need it"));

        await t.Session.CancelPackageRequestAsync(Home, created.Id, CancellationToken.None);
        t.Packages.Cancelled.Should().Equal("req-new");
    }

    [Fact]
    public async Task ProbeIsFalseForFirstPartyAndNullForOpaqueTokens()
    {
        using var t = new TestSession();
        var cli = TestSession.Account with { SignInMethod = SignInMethod.AzureCLI };
        (await t.Session.ProbeAccessPackagesAsync(cli, "t1")).Should().BeFalse();
        // The fake token provider hands out the opaque string "token".
        (await t.Session.ProbeAccessPackagesAsync(TestSession.Account, "t1")).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test cli/Elevate.Cli.sln --filter "FullyQualifiedName~PackageSessionTests" 2>&1 | tail -15`
Expected: build errors (no `Packages` on `TestSession`, no session methods).

- [ ] **Step 3: Implement**

In `Session/ElevateSession.cs`: add a constructor parameter `IAccessPackageProvider? accessPackages = null` after `approvalProviders`, set `Packages = accessPackages ?? new AccessPackageProvider(http, tokens);` in the body, and add the property `public IAccessPackageProvider Packages { get; }` after `ApprovalProviders`.

Create `cli/src/Elevate.Cli/Session/ElevateSession.AccessPackages.cs`:

```csharp
using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.Cli.Session;

/// <summary>What one tenant reported for the signed-in user: the packages they may request, their requests and assignments.</summary>
public sealed record TenantPackages(
    TenantKey Key,
    IReadOnlyList<AccessPackage> Packages,
    IReadOnlyList<AccessPackageRequest> Requests,
    IReadOnlyList<AccessPackageAssignment> Assignments);

/// <summary>
/// Access packages (entitlement management) per tenant. A headless port of the macOS
/// <c>AppModel+AccessPackages</c> without the polling, the diff and the notifications: every
/// command reads the service directly and nothing is stored beyond the tenant's availability flag.
/// </summary>
public sealed partial class ElevateSession
{
    /// <summary>Why access packages cannot be read in this tenant, or null when they can be tried.</summary>
    public string? AccessPackagesUnavailableReason(TenantKey key)
    {
        if (Identity(key.IdentityId) is not { } identity)
        {
            return "That account is no longer signed in";
        }

        if (!identity.SignInMethod.IsPreauthorisedForEntraActivation)
        {
            return $"The {identity.SignInMethod.DisplayName} cannot read access packages; sign in with your own app registration.";
        }

        if (Tenant(key)?.AccessPackagesAvailable == false)
        {
            return "Access packages are not permitted in this tenant. Sign in again to consent to EntitlementMgmt-SubjectAccess.ReadWrite, or use 'elevate tenants retry'.";
        }

        return null;
    }

    /// <summary>Tracked tenants that may hold access packages, narrowed by the account and tenant filters.</summary>
    public IReadOnlyList<TenantKey> AccessPackageTenants(string? account, string? tenant) =>
        [.. Tenants
            .Where(t => string.IsNullOrWhiteSpace(account) || AccountMatches(t.IdentityId, account))
            .Where(t => string.IsNullOrWhiteSpace(tenant)
                || t.DisplayName.Contains(tenant, StringComparison.OrdinalIgnoreCase)
                || t.TenantId.Equals(tenant, StringComparison.OrdinalIgnoreCase))
            .Where(t => AccessPackagesUnavailableReason(t.Key) is null)
            .Select(t => t.Key)];

    private bool AccountMatches(string identityId, string account) =>
        Identity(identityId) is { } i
        && (i.Upn.Contains(account, StringComparison.OrdinalIgnoreCase)
            || i.DisplayName.Contains(account, StringComparison.OrdinalIgnoreCase)
            || i.Id.Equals(account, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the user's requests and assignments (and the requestable packages when asked). A
    /// success marks the tenant available; a consent or permission refusal marks it unavailable
    /// and rethrows so the command can say so.
    /// </summary>
    public async Task<TenantPackages> ReadPackagesAsync(TenantKey key, bool includePackages, CancellationToken ct)
    {
        var (identity, tenant) = Require(key);
        try
        {
            var requests = await Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.MyRequestsAsync(identity, key.TenantId, ct), ct).ConfigureAwait(false);
            var assignments = await Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.MyAssignmentsAsync(identity, key.TenantId, ct), ct).ConfigureAwait(false);
            IReadOnlyList<AccessPackage> packages = includePackages
                ? await Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.RequestablePackagesAsync(identity, key.TenantId, ct), ct).ConfigureAwait(false)
                : [];
            if (tenant.AccessPackagesAvailable != true)
            {
                Upsert(tenant with { AccessPackagesAvailable = true });
            }

            return new TenantPackages(key, packages, requests, assignments);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.ConsentRequired or PimErrorKind.Forbidden)
        {
            if (tenant.AccessPackagesAvailable != false)
            {
                Upsert(tenant with { AccessPackagesAvailable = false });
            }

            throw;
        }
    }

    public Task<IReadOnlyList<PolicyRequirement>> PackageRequirementsAsync(TenantKey key, string packageId, CancellationToken ct)
    {
        var (identity, _) = Require(key);
        return Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.RequirementsAsync(packageId, identity, key.TenantId, ct), ct);
    }

    public Task<AccessPackageRequest> RequestPackageAsync(TenantKey key, string packageId, string? policyId, string justification, CancellationToken ct)
    {
        var (identity, _) = Require(key);
        return Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.RequestAsync(packageId, policyId, justification, identity, key.TenantId, ct), ct);
    }

    public Task CancelPackageRequestAsync(TenantKey key, string requestId, CancellationToken ct)
    {
        var (identity, _) = Require(key);
        return Acquire(identity, key.TenantId, Packages.Scopes, async () =>
        {
            await Packages.CancelAsync(requestId, identity, key.TenantId, ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>
    /// Whether the cached Graph token for this tenant carries the entitlement scope. False for the
    /// first-party apps, which never do; null when nothing can be told without a prompt.
    /// Mirror of the macOS <c>probeAccessPackages</c>.
    /// </summary>
    public async Task<bool?> ProbeAccessPackagesAsync(Identity identity, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.SignInMethod.IsPreauthorisedForEntraActivation)
        {
            return false;
        }

        string token;
        try
        {
            token = await Tokens.AccessTokenAsync(identity, tenantId, Scopes.EntitlementAll, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        return AccessTokenClaims.PermitsEntitlementSelfService(token);
    }

    private (Identity Identity, TenantContext Tenant) Require(TenantKey key)
    {
        var identity = Identity(key.IdentityId) ?? throw new PimException(PimErrorKind.Unexpected, "That account is no longer signed in");
        var tenant = Tenant(key) ?? throw new PimException(PimErrorKind.Unexpected, "That tenant is not tracked");
        return (identity, tenant);
    }
}
```

Note: `Acquire` and `Upsert` already exist as private members in `ElevateSession.Refresh.cs`; partial classes share them.

- [ ] **Step 4: Run the CLI suite**

Run: `cd /Users/frode.hus/pimtray && dotnet test cli/Elevate.Cli.sln 2>&1 | tail -5`
Expected: `Passed!`, no failures (7 new tests).

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add cli/src/Elevate.Cli/Session cli/tests/Elevate.Cli.Tests/Support cli/tests/Elevate.Cli.Tests/PackageSessionTests.cs && git commit -q -m "CLI: session reads, requests and cancels access packages per tenant"
```

---

### Task 8: DTOs, short ids and tables

**Files:**
- Modify: `cli/src/Elevate.Cli/Selection/ShortId.cs`
- Modify: `cli/src/Elevate.Cli/Rendering/Views.cs`
- Test: `cli/tests/Elevate.Cli.Tests/ViewsTests.cs`

**Interfaces:**
- Consumes: `TenantPackages` (Task 7), the models.
- Produces:
  - `ShortId.For(TenantKey key, string id)`: eight lower-case hex characters from `identityId|tenantId|id`.
  - `Dto.Package(string Id, string PackageId, string Name, string? Description, bool Hidden, string TenantId, string Tenant, string Account, string? State, string? RequestId, string? AssignmentId)`
  - `Dto.PackageRequest(string Id, string RequestId, string PackageId, string Package, string State, string? Status, string? Justification, DateTimeOffset? RequestedAt, DateTimeOffset? CompletedAt, string? PolicyId, string TenantId, string Tenant, string Account, bool Cancellable)`
  - `Dto.PackageAssignment(string Id, string AssignmentId, string PackageId, string Package, string State, string? Policy, DateTimeOffset? ExpiresAt, string? ExpiresIn, string TenantId, string Tenant, string Account)`
  - `Dto.PolicyOption(string Id, string Name, string? Description, bool RequiresApproval, bool RequiresAnswers)`
  - `Views.RequestStateName(AccessPackageRequestState)`, `Views.AssignmentStateName(AccessPackageAssignmentState)` (camelCase strings, `"unknown"` fallback)
  - `Views.PackageState(TenantPackages read, string packageId) : (string? Text, string? RequestId, string? AssignmentId)`: a delivered assignment wins (`"delivered"`), else the newest open request's state name, else null.
  - `Views.Package(ElevateSession, TenantPackages, AccessPackage)`, `Views.PackageRequest(ElevateSession, TenantKey, AccessPackageRequest)`, `Views.PackageAssignment(ElevateSession, TenantKey, AccessPackageAssignment, DateTimeOffset now)`, `Views.PolicyOption(PolicyRequirement)`
  - `Views.PackagesTable(ElevateSession, IReadOnlyList<TenantPackages>, bool showAccount)`, `Views.PackageRequestsTable(ElevateSession, IEnumerable<(TenantKey Key, AccessPackageRequest Request)>, DateTimeOffset now, bool all, bool showAccount)`, `Views.PackageAssignmentsTable(ElevateSession, IEnumerable<(TenantKey Key, AccessPackageAssignment Assignment)>, DateTimeOffset now, bool showAccount)`
  - `Views.RequestStateMarkup(AccessPackageRequestState)`: yellow for open states, green delivered, red denied/deliveryFailed, grey canceled/unknown.

- [ ] **Step 1: Write the failing tests**

Append to `ViewsTests.cs` (add `using Elevate.Cli.Selection;`, `using Elevate.Cli.Session;` and `using Elevate.Core.Models;` if missing):

```csharp
    private static readonly TenantKey Home = TestSession.Tenant.Key;

    [Fact]
    public void ShortIdForATenantScopedIdIsStableAndDistinct()
    {
        var a = ShortId.For(Home, "req-1");
        a.Should().HaveLength(8);
        a.Should().Be(ShortId.For(Home, "req-1"));
        a.Should().NotBe(ShortId.For(new TenantKey("id1", "t2"), "req-1"));
        ShortId.LooksLikeId(a).Should().BeTrue();
    }

    [Fact]
    public void PackageStateComesFromAssignmentsThenOpenRequests()
    {
        var read = new TenantPackages(Home,
            [new AccessPackage("p1", "One"), new AccessPackage("p2", "Two"), new AccessPackage("p3", "Three"), new AccessPackage("p4", "Four")],
            [
                new AccessPackageRequest("r1", "p1", "One", "userAdd", AccessPackageRequestState.PendingApproval, CreatedAt: DateTimeOffset.UtcNow),
                new AccessPackageRequest("r2", "p2", "Two", "userAdd", AccessPackageRequestState.Denied),
                new AccessPackageRequest("r3", "p3", "Three", "userAdd", AccessPackageRequestState.PendingApproval),
            ],
            [new AccessPackageAssignment("a3", "p3", "Three", AccessPackageAssignmentState.Delivered)]);

        Views.PackageState(read, "p1").Should().Be(("pendingApproval", "r1", null));
        Views.PackageState(read, "p2").Should().Be((null, null, null));
        Views.PackageState(read, "p3").Should().Be(("delivered", null, "a3"));
        Views.PackageState(read, "p4").Should().Be((null, null, null));
    }

    [Fact]
    public void PackageDtosCarryTenantAccountAndStableIds()
    {
        using var t = new TestSession();
        var now = DateTimeOffset.UtcNow;
        var request = new AccessPackageRequest("req-1", "pkg-sandbox", "Azure Sandbox Contributor", "userAdd", AccessPackageRequestState.PendingApproval,
            "PendingApproval", "Need it", now.AddHours(-1), null, "pol-eng");
        var assignment = new AccessPackageAssignment("asg-1", "pkg-exchange", "Exchange Operations", AccessPackageAssignmentState.Delivered, "On-call staff", now.AddDays(3));
        var read = new TenantPackages(Home, [new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor", "30 days.", IsHidden: true)], [request], [assignment]);

        var package = Views.Package(t.Session, read, read.Packages[0]);
        package.Id.Should().Be(ShortId.For(Home, "pkg-sandbox"));
        package.Tenant.Should().Be("Contoso");
        package.Account.Should().Be("alex@contoso.com");
        package.Hidden.Should().BeTrue();
        package.State.Should().Be("pendingApproval");
        package.RequestId.Should().Be("req-1");

        var dto = Views.PackageRequest(t.Session, Home, request);
        dto.Id.Should().Be(ShortId.For(Home, "req-1"));
        dto.RequestId.Should().Be("req-1");
        dto.State.Should().Be("pendingApproval");
        dto.Cancellable.Should().BeTrue();
        dto.PolicyId.Should().Be("pol-eng");

        var asg = Views.PackageAssignment(t.Session, Home, assignment, now);
        asg.Id.Should().Be(ShortId.For(Home, "asg-1"));
        asg.State.Should().Be("delivered");
        asg.Policy.Should().Be("On-call staff");
        asg.ExpiresIn.Should().Be("3 d");

        var json = JsonSerializer.Serialize(dto, Output.JsonOptions);
        json.Should().Contain("\"state\": \"pendingApproval\"").And.Contain("\"cancellable\": true");
    }

    [Fact]
    public void PackageTablesRenderWithoutThrowing()
    {
        using var t = new TestSession();
        var now = DateTimeOffset.UtcNow;
        var request = new AccessPackageRequest("req-1", "pkg-sandbox", "Azure Sandbox Contributor", "userAdd", AccessPackageRequestState.Canceled, "Canceled by requestor", "Need it", now.AddDays(-1), now);
        var assignment = new AccessPackageAssignment("asg-1", "pkg-exchange", "Exchange Operations", AccessPackageAssignmentState.Delivered, null, null);
        var read = new TenantPackages(Home, [new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor", null, IsHidden: true)], [request], [assignment]);

        Views.PackagesTable(t.Session, [read], showAccount: true).Rows.Count.Should().Be(1);
        Views.PackageRequestsTable(t.Session, [(Home, request)], now, all: true, showAccount: false).Rows.Count.Should().Be(1);
        Views.PackageAssignmentsTable(t.Session, [(Home, assignment)], now, showAccount: false).Rows.Count.Should().Be(1);
        Views.RequestStateMarkup(AccessPackageRequestState.Denied).Should().StartWith("[red]");
        Views.RequestStateMarkup(AccessPackageRequestState.Delivered).Should().StartWith("[green]");
        Views.RequestStateMarkup(AccessPackageRequestState.Submitted).Should().StartWith("[yellow]");
        Views.RequestStateMarkup(AccessPackageRequestState.Canceled).Should().StartWith("[grey]");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test cli/Elevate.Cli.sln --filter "FullyQualifiedName~ViewsTests" 2>&1 | tail -15`
Expected: build errors.

- [ ] **Step 3: Implement**

In `Selection/ShortId.cs`, add after `For(ApprovalRequest)`:

```csharp
    /// <summary>For an access package, request or assignment id inside one tenant.</summary>
    public static string For(TenantKey key, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Hash(key.IdentityId + "|" + key.TenantId + "|" + id);
    }
```

In `Rendering/Views.cs`, add to `Dto`:

```csharp
    public sealed record Package(
        string Id, string PackageId, string Name, string? Description, bool Hidden, string TenantId, string Tenant, string Account,
        string? State, string? RequestId, string? AssignmentId);

    public sealed record PackageRequest(
        string Id, string RequestId, string PackageId, string Package, string State, string? Status, string? Justification,
        DateTimeOffset? RequestedAt, DateTimeOffset? CompletedAt, string? PolicyId, string TenantId, string Tenant, string Account, bool Cancellable);

    public sealed record PackageAssignment(
        string Id, string AssignmentId, string PackageId, string Package, string State, string? Policy, DateTimeOffset? ExpiresAt, string? ExpiresIn,
        string TenantId, string Tenant, string Account);

    public sealed record PolicyOption(string Id, string Name, string? Description, bool RequiresApproval, bool RequiresAnswers);
```

Add to `Views` (with `using Elevate.Cli.Session;` already present) a new `// MARK: Access packages` section:

```csharp
    public static string RequestStateName(AccessPackageRequestState state) => state switch
    {
        AccessPackageRequestState.Submitted => "submitted",
        AccessPackageRequestState.PendingApproval => "pendingApproval",
        AccessPackageRequestState.Delivering => "delivering",
        AccessPackageRequestState.Delivered => "delivered",
        AccessPackageRequestState.DeliveryFailed => "deliveryFailed",
        AccessPackageRequestState.Denied => "denied",
        AccessPackageRequestState.Scheduled => "scheduled",
        AccessPackageRequestState.Canceled => "canceled",
        AccessPackageRequestState.PartiallyDelivered => "partiallyDelivered",
        _ => "unknown",
    };

    public static string AssignmentStateName(AccessPackageAssignmentState state) => state switch
    {
        AccessPackageAssignmentState.Delivering => "delivering",
        AccessPackageAssignmentState.Delivered => "delivered",
        AccessPackageAssignmentState.Expired => "expired",
        _ => "unknown",
    };

    /// <summary>The state caption for a requestable package: a delivered assignment wins, then the newest open request.</summary>
    public static (string? Text, string? RequestId, string? AssignmentId) PackageState(TenantPackages read, string packageId)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.Assignments.FirstOrDefault(a => a.PackageId == packageId && a.State == AccessPackageAssignmentState.Delivered) is { } delivered)
        {
            return ("delivered", null, delivered.Id);
        }

        var open = read.Requests
            .Where(r => r.PackageId == packageId && r.State.IsOpen())
            .OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        return open is null ? (null, null, null) : (RequestStateName(open.State), open.Id, null);
    }

    public static Dto.Package Package(ElevateSession session, TenantPackages read, AccessPackage package)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(package);
        var (state, requestId, assignmentId) = PackageState(read, package.Id);
        return new Dto.Package(ShortId.For(read.Key, package.Id), package.Id, package.DisplayName, package.Description, package.IsHidden,
            read.Key.TenantId, session.TenantName(read.Key), session.AccountName(read.Key.IdentityId), state, requestId, assignmentId);
    }

    public static Dto.PackageRequest PackageRequest(ElevateSession session, TenantKey key, AccessPackageRequest r)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(r);
        return new Dto.PackageRequest(ShortId.For(key, r.Id), r.Id, r.PackageId, r.PackageName, RequestStateName(r.State), r.Status, r.Justification,
            r.CreatedAt, r.CompletedAt, r.PolicyId, key.TenantId, session.TenantName(key), session.AccountName(key.IdentityId), r.State.IsCancellable());
    }

    public static Dto.PackageAssignment PackageAssignment(ElevateSession session, TenantKey key, AccessPackageAssignment a, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(a);
        var left = a.ExpiresAt is { } end && end > now ? Countdown.Label(end - now) : null;
        return new Dto.PackageAssignment(ShortId.For(key, a.Id), a.Id, a.PackageId, a.PackageName, AssignmentStateName(a.State), a.PolicyName, a.ExpiresAt, left,
            key.TenantId, session.TenantName(key), session.AccountName(key.IdentityId));
    }

    public static Dto.PolicyOption PolicyOption(PolicyRequirement p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new Dto.PolicyOption(p.Id, p.DisplayName, p.Description, p.IsApprovalRequired, p.RequiresAnswers);
    }

    public static string RequestStateMarkup(AccessPackageRequestState state) => state switch
    {
        AccessPackageRequestState.Submitted => "[yellow]submitted[/]",
        AccessPackageRequestState.PendingApproval => "[yellow]awaiting approval[/]",
        AccessPackageRequestState.Delivering => "[yellow]delivering[/]",
        AccessPackageRequestState.Scheduled => "[yellow]scheduled[/]",
        AccessPackageRequestState.PartiallyDelivered => "[yellow]partially delivered[/]",
        AccessPackageRequestState.Delivered => "[green]delivered[/]",
        AccessPackageRequestState.Denied => "[red]denied[/]",
        AccessPackageRequestState.DeliveryFailed => "[red]delivery failed[/]",
        AccessPackageRequestState.Canceled => "[grey]canceled[/]",
        _ => "[grey]unknown[/]",
    };

    private static string LocalDate(DateTimeOffset? date) =>
        date is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "—";

    public static Table PackagesTable(ElevateSession session, IReadOnlyList<TenantPackages> reads, bool showAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reads);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Package");
        table.AddColumn("Description");
        table.AddColumn("Tenant");
        if (showAccount)
        {
            table.AddColumn("Account");
        }

        table.AddColumn("State");
        foreach (var read in reads)
        {
            foreach (var p in read.Packages.OrderBy(p => p.DisplayName, StringComparer.Ordinal))
            {
                var (state, _, _) = PackageState(read, p.Id);
                var name = Markup.Escape(p.DisplayName);
                if (p.IsHidden)
                {
                    name += " [grey](hidden)[/]";
                }

                var cells = new List<string> { $"[grey]{ShortId.For(read.Key, p.Id)}[/]", name, Markup.Escape(p.Description ?? "—"), Markup.Escape(session.TenantName(read.Key)) };
                if (showAccount)
                {
                    cells.Add(Markup.Escape(session.AccountName(read.Key.IdentityId)));
                }

                cells.Add(state switch
                {
                    null => "[grey]—[/]",
                    "delivered" => "[green]delivered[/]",
                    _ => RequestStateMarkup(AccessPackageRequestStates.Parse(state)),
                });
                table.AddRow(cells.ToArray());
            }
        }

        return table;
    }

    public static Table PackageRequestsTable(ElevateSession session, IEnumerable<(TenantKey Key, AccessPackageRequest Request)> requests, DateTimeOffset now, bool all, bool showAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(requests);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Package");
        table.AddColumn("Tenant");
        if (showAccount)
        {
            table.AddColumn("Account");
        }

        table.AddColumn("State");
        table.AddColumn("Requested");
        if (all)
        {
            table.AddColumn("Completed");
        }

        table.AddColumn("Reason");
        foreach (var (key, r) in requests)
        {
            var state = RequestStateMarkup(r.State);
            if (all && r.State is AccessPackageRequestState.DeliveryFailed or AccessPackageRequestState.Canceled && !string.IsNullOrWhiteSpace(r.Status))
            {
                state += $" [grey]{Markup.Escape(r.Status)}[/]";
            }

            var cells = new List<string> { $"[grey]{ShortId.For(key, r.Id)}[/]", Markup.Escape(r.PackageName), Markup.Escape(session.TenantName(key)) };
            if (showAccount)
            {
                cells.Add(Markup.Escape(session.AccountName(key.IdentityId)));
            }

            cells.Add(state);
            cells.Add(r.CreatedAt is { } at ? Markup.Escape(Countdown.Label(now - at) + " ago") : "—");
            if (all)
            {
                cells.Add(Markup.Escape(LocalDate(r.CompletedAt)));
            }

            cells.Add(Markup.Escape(r.Justification ?? "—"));
            table.AddRow(cells.ToArray());
        }

        return table;
    }

    public static Table PackageAssignmentsTable(ElevateSession session, IEnumerable<(TenantKey Key, AccessPackageAssignment Assignment)> assignments, DateTimeOffset now, bool showAccount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assignments);
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[grey]ID[/]");
        table.AddColumn("Package");
        table.AddColumn("Tenant");
        if (showAccount)
        {
            table.AddColumn("Account");
        }

        table.AddColumn("Policy");
        table.AddColumn("Expires");
        foreach (var (key, a) in assignments)
        {
            string expires;
            if (a.ExpiresAt is { } end)
            {
                var left = end - now;
                var color = left <= TimeSpan.FromDays(7) ? "orange1" : "grey";
                expires = left > TimeSpan.Zero
                    ? $"{Markup.Escape(LocalDate(end))} [{color}]in {Markup.Escape(Countdown.Label(left))}[/]"
                    : $"{Markup.Escape(LocalDate(end))} [red]expired[/]";
            }
            else
            {
                expires = "[grey]No expiry[/]";
            }

            var cells = new List<string> { $"[grey]{ShortId.For(key, a.Id)}[/]", Markup.Escape(a.PackageName), Markup.Escape(session.TenantName(key)) };
            if (showAccount)
            {
                cells.Add(Markup.Escape(session.AccountName(key.IdentityId)));
            }

            cells.Add(Markup.Escape(a.PolicyName ?? "—"));
            cells.Add(expires);
            table.AddRow(cells.ToArray());
        }

        return table;
    }
```

Check `Countdown.Label(TimeSpan)` output for three days: read `windows/src/Elevate.Core/Support/Countdown.cs`; if it renders days differently from `"3 d"`, change the assertion in the test to whatever the existing label produces (the label helper is shared; do not change it).

- [ ] **Step 4: Run the CLI suite**

Run: `cd /Users/frode.hus/pimtray && dotnet test cli/Elevate.Cli.sln 2>&1 | tail -5`
Expected: `Passed!`, no failures.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add cli/src/Elevate.Cli/Selection/ShortId.cs cli/src/Elevate.Cli/Rendering/Views.cs cli/tests/Elevate.Cli.Tests/ViewsTests.cs && git commit -q -m "CLI: access package DTOs, short ids and tables"
```

---

### Task 9: The `packages` command group

**Files:**
- Create: `cli/src/Elevate.Cli/Commands/PackageCommands.cs`
- Modify: `cli/src/Elevate.Cli/Program.cs`
- Test: `cli/tests/Elevate.Cli.Tests/CommandTreeTests.cs`

**Interfaces:**
- Consumes: Task 7 session methods, Task 8 views, `CommandContext`, `CommonOptions.Account/Tenant`, `Output`, `CliException`/`ExitCodes`, `AccessPackageProvider.MyAccessUrl`.
- Produces: `PackageCommands.Packages()` returning the `packages` command with subcommands `list`, `requests`, `assigned`, `request`, `cancel`. Bare `elevate packages` behaves like `list`.

Behaviour:

- Tenant selection: `--account`/`-a` and `--tenant`/`-t` (from `CommonOptions`) narrow `session.AccessPackageTenants`. Zero candidates: `CliException` (exit `NotFound`) whose message names why. When every tracked tenant is excluded, the message is the first tenant's `AccessPackagesUnavailableReason`, so an Azure CLI account learns it needs the own-app registration.
- Reads: each candidate tenant is read through `ReadPackagesAsync`; a failure is reported as a warning (`Output.Warn`, "Contoso: <message>") and that tenant is skipped, mirroring `RoleCommands.ReportTenantErrors`. If every tenant failed, the exit code is `Failure`.
- `list [--all]`: table of requestable packages with the State column; `--json` prints `Dto.Package[]`. Hidden packages are included with "(hidden)".
- `requests [--all]`: open requests by default (`State.IsOpen()`), newest first; `--all` includes every request with the Completed column and Graph's status text for failed and canceled ones. `--json` prints `Dto.PackageRequest[]`.
- `assigned`: delivered assignments (`State == Delivered`), soonest expiry first, then name. `--json` prints `Dto.PackageAssignment[]`.
- `request <package> --justification|-j <text> [--policy <id or name>]`: `<package>` is a short id from `list`, the Graph package id, or a name matched exactly then as a substring (case-insensitive) across the candidate tenants' requestable packages; several matches list them (exit `NotFound`). Then `PackageRequirementsAsync`: none → `CliException("No policy lets you request <name>.", Failure)`; one → use it; several without `--policy` → `CliException` listing `id — name (approval / no approval)` per line, exit `Usage`; `--policy` matches by id or name (exact, then substring, case-insensitive). If the chosen policy `RequiresAnswers` → `CliException($"<name> asks questions that Elevate does not collect. Request it in My Access: <MyAccessUrl>", Failure)`. Justification: `--justification` trimmed; when empty, prompt (`TextPrompt<string>`) if `CanPrompt`, else `CliException("… needs a justification: pass --justification.", Usage)`. On success print `[green]Requested[/] <name> in <tenant>: <state markup>` and the note `'elevate packages requests' follows it.`; `--json` prints the `Dto.PackageRequest`.
- `cancel <request…>`: each argument is a short id from `requests` or a Graph request id; resolved against the candidate tenants' open requests (`ReadPackagesAsync(key, includePackages: false)`). A request whose state is not cancellable → note "<name>: cannot be cancelled once delivery started." and skip. Prints `[green]Cancelled[/] <name>.` per request; failures counted; exit `Ok`, `Partial` or `Failure` like `ActivationCommands.Cancel`.

- [ ] **Step 1: Write the failing tests**

In `CommandTreeTests.cs`: add `"packages"` to the expected top-level list in `EveryTopLevelCommandIsPresent`, and these `InlineData` rows to `CommandLinesParseWithoutErrors`:

```csharp
    [InlineData("packages list --tenant contoso --json")]
    [InlineData("packages requests --all")]
    [InlineData("packages assigned -a alex")]
    [InlineData("packages request \"Azure Sandbox\" --justification \"INC-4412\" --policy Engineers")]
    [InlineData("packages cancel 0123abcd 4567ef01")]
```

Add a new test:

```csharp
    [Fact]
    public void PackagesSubcommandsArePresent()
    {
        var packages = Program.BuildRootCommand().Subcommands.Single(c => c.Name == "packages");
        packages.Subcommands.Select(c => c.Name).Should().BeEquivalentTo(["list", "requests", "assigned", "request", "cancel"]);
        Program.BuildRootCommand().Parse("packages request").Errors.Should().NotBeEmpty("the package argument is required");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /Users/frode.hus/pimtray && dotnet test cli/Elevate.Cli.sln --filter "FullyQualifiedName~CommandTreeTests" 2>&1 | tail -15`
Expected: the top-level and subcommand tests fail (no `packages`).

- [ ] **Step 3: Implement**

Create `cli/src/Elevate.Cli/Commands/PackageCommands.cs`:

```csharp
using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>packages</c>: entitlement management access packages, the terminal counterpart of the apps' access packages window.</summary>
public static class PackageCommands
{
    public static Command Packages()
    {
        var command = new Command("packages", "Access packages you can request, have requested or hold. Bare 'packages' lists the requestable ones.");
        var list = List();
        command.SetAction((parse, ct) => list.Action!.InvokeAsync(parse, ct));
        command.Subcommands.Add(list);
        command.Subcommands.Add(Requests());
        command.Subcommands.Add(Assigned());
        command.Subcommands.Add(Request());
        command.Subcommands.Add(Cancel());
        return command;
    }

    private static Command List()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var command = new Command("list", "Packages you may request, with the state of any pending request or delivered assignment.") { account, tenant };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var (reads, failures) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: true, ct).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(reads.SelectMany(r => r.Packages.Select(p => Views.Package(session, r, p))).ToList());
            }
            else if (reads.All(r => r.Packages.Count == 0))
            {
                if (reads.Count > 0)
                {
                    context.Output.Plain("No access packages are available to request.");
                }
            }
            else
            {
                context.Output.Write(Views.PackagesTable(session, reads, session.Identities.Count > 1));
                context.Output.Note("'elevate packages request <package> --justification …' to request one.");
            }

            return Exit(reads.Count, failures);
        });
        return command;
    }

    private static Command Requests()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var all = new Option<bool>("--all") { Description = "Include denied, failed and cancelled requests, with their completion date and the service's status text." };
        var command = new Command("requests", "Your own package requests; open ones by default.") { account, tenant, all };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var showAll = parse.GetValue(all);
            var (reads, failures) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: false, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var rows = reads
                .SelectMany(r => r.Requests.Where(q => showAll || q.State.IsOpen()).Select(q => (r.Key, Request: q)))
                .OrderByDescending(x => x.Request.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.Request.PackageName, StringComparer.Ordinal)
                .ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(rows.Select(x => Views.PackageRequest(session, x.Key, x.Request)).ToList());
            }
            else if (rows.Count == 0)
            {
                if (reads.Count > 0)
                {
                    context.Output.Plain(showAll ? "No package requests." : "No open package requests. '--all' includes finished ones.");
                }
            }
            else
            {
                context.Output.Write(Views.PackageRequestsTable(session, rows, now, showAll, session.Identities.Count > 1));
                if (rows.Any(x => x.Request.State.IsCancellable()))
                {
                    context.Output.Note("'elevate packages cancel <id>' withdraws a request that is still awaiting approval.");
                }
            }

            return Exit(reads.Count, failures);
        });
        return command;
    }

    private static Command Assigned()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var command = new Command("assigned", "Packages delivered to you, with expiry and policy.") { account, tenant };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var (reads, failures) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: false, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var rows = reads
                .SelectMany(r => r.Assignments.Where(a => a.State == AccessPackageAssignmentState.Delivered).Select(a => (r.Key, Assignment: a)))
                .OrderBy(x => x.Assignment.ExpiresAt ?? DateTimeOffset.MaxValue)
                .ThenBy(x => x.Assignment.PackageName, StringComparer.Ordinal)
                .ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(rows.Select(x => Views.PackageAssignment(session, x.Key, x.Assignment, now)).ToList());
            }
            else if (rows.Count == 0)
            {
                if (reads.Count > 0)
                {
                    context.Output.Plain("No access packages are assigned to you.");
                }
            }
            else
            {
                context.Output.Write(Views.PackageAssignmentsTable(session, rows, now, session.Identities.Count > 1));
            }

            return Exit(reads.Count, failures);
        });
        return command;
    }

    private static Command Request()
    {
        var package = new Argument<string>("package") { Description = "A package name or id from 'elevate packages list'." };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var justification = new Option<string?>("--justification", "-j") { Description = "Why you need it; prompted in a terminal when omitted." };
        var policy = new Option<string?>("--policy", "-p") { Description = "The policy to request under (id or name); needed only when several apply." };
        var command = new Command("request", "Request an access package. Packages whose policy asks questions must be requested in My Access.") { package, account, tenant, justification, policy };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var (reads, _) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: true, ct).ConfigureAwait(false);
            var (key, chosen) = ResolvePackage(session, reads, parse.GetValue(package)!);

            var requirements = await context.Output.StatusAsync($"Checking the policies for {chosen.DisplayName}…",
                () => session.PackageRequirementsAsync(key, chosen.Id, ct)).ConfigureAwait(false);
            var selected = ChoosePolicy(requirements, parse.GetValue(policy), chosen.DisplayName);
            if (selected.RequiresAnswers)
            {
                throw new CliException(
                    $"{chosen.DisplayName} asks questions that Elevate does not collect. Request it in My Access: {AccessPackageProvider.MyAccessUrl(key.TenantId, chosen.Id)}",
                    ExitCodes.Failure);
            }

            var reason = (parse.GetValue(justification) ?? string.Empty).Trim();
            if (reason.Length == 0)
            {
                if (!context.Output.CanPrompt)
                {
                    throw new CliException($"{chosen.DisplayName} needs a justification: pass --justification.", ExitCodes.Usage);
                }

                reason = context.Output.Stderr.Prompt(new TextPrompt<string>($"Justification for [bold]{Markup.Escape(chosen.DisplayName)}[/]:")).Trim();
            }

            var created = await context.Output.StatusAsync($"Requesting {chosen.DisplayName}…",
                () => session.RequestPackageAsync(key, chosen.Id, selected.Id, reason, ct)).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(Views.PackageRequest(session, key, created));
                return ExitCodes.Ok;
            }

            context.Output.WriteLine($"[green]Requested[/] {Markup.Escape(chosen.DisplayName)} in {Markup.Escape(session.TenantName(key))}: {Views.RequestStateMarkup(created.State)}.");
            context.Output.Note("'elevate packages requests' follows it.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Cancel()
    {
        var requests = new Argument<string[]>("request") { Description = "Ids from 'elevate packages requests'.", Arity = ArgumentArity.OneOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var command = new Command("cancel", "Withdraw package requests that are still awaiting approval.") { requests, account, tenant };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var (reads, _) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: false, ct).ConfigureAwait(false);
            var failures = 0;
            var attempted = 0;
            foreach (var term in parse.GetValue(requests) ?? [])
            {
                var (key, request) = ResolveRequest(session, reads, term);
                if (!request.State.IsCancellable())
                {
                    context.Output.Note($"{Markup.Escape(request.PackageName)}: cannot be cancelled once delivery started.");
                    continue;
                }

                attempted += 1;
                try
                {
                    await context.Output.StatusAsync($"Cancelling {request.PackageName}…", () => session.CancelPackageRequestAsync(key, request.Id, ct)).ConfigureAwait(false);
                    context.Output.WriteLine($"[green]Cancelled[/] {Markup.Escape(request.PackageName)}.");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures += 1;
                    context.Output.Error($"{request.PackageName}: {ElevateSession.Describe(e)}");
                }
            }

            return failures == 0 ? ExitCodes.Ok : failures == attempted ? ExitCodes.Failure : ExitCodes.Partial;
        });
        return command;
    }

    /// <summary>Reads every candidate tenant; a tenant that refuses is warned about and left out.</summary>
    private static async Task<(IReadOnlyList<TenantPackages> Reads, int Failures)> ReadAsync(CommandContext context, string? account, string? tenant, bool includePackages, CancellationToken ct)
    {
        var session = context.Session;
        var keys = session.AccessPackageTenants(account, tenant);
        if (keys.Count == 0)
        {
            var excluded = session.Tenants.Select(t => session.AccessPackagesUnavailableReason(t.Key)).FirstOrDefault(r => r is not null);
            throw new CliException(excluded ?? "No tracked tenant matches. Run 'elevate tenants' to list them.", ExitCodes.NotFound);
        }

        var reads = new List<TenantPackages>();
        var failures = 0;
        var title = keys.Count == 1 ? $"Reading access packages in {session.TenantName(keys[0])}…" : $"Reading access packages in {keys.Count} tenants…";
        await context.Output.StatusAsync(title, async () =>
        {
            foreach (var key in keys)
            {
                try
                {
                    reads.Add(await session.ReadPackagesAsync(key, includePackages, ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    failures += 1;
                    context.Output.Warn($"{Markup.Escape(session.TenantName(key))}: {Markup.Escape(ElevateSession.Describe(e))}");
                }
            }
        }).ConfigureAwait(false);
        return (reads, failures);
    }

    private static int Exit(int reads, int failures) => failures == 0 ? ExitCodes.Ok : reads == 0 ? ExitCodes.Failure : ExitCodes.Partial;

    /// <summary>A short id, a Graph id, an exact name, then a substring, across the tenants read.</summary>
    internal static (TenantKey Key, AccessPackage Package) ResolvePackage(ElevateSession session, IReadOnlyList<TenantPackages> reads, string term)
    {
        var all = reads.SelectMany(r => r.Packages.Select(p => (r.Key, Package: p))).ToList();
        var byId = all.Where(x => ShortId.For(x.Key, x.Package.Id) == term || x.Package.Id.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = byId.Count > 0 ? byId : all.Where(x => x.Package.DisplayName.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = all.Where(x => x.Package.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No requestable access package matches '{term}'. 'elevate packages list' shows them.", ExitCodes.NotFound),
            _ => throw new CliException($"'{term}' matches several packages; use the id: " + string.Join(", ",
                matches.Select(x => $"{x.Package.DisplayName} ({ShortId.For(x.Key, x.Package.Id)}, {session.TenantName(x.Key)})")), ExitCodes.NotFound),
        };
    }

    internal static (TenantKey Key, AccessPackageRequest Request) ResolveRequest(ElevateSession session, IReadOnlyList<TenantPackages> reads, string term)
    {
        var all = reads.SelectMany(r => r.Requests.Select(q => (r.Key, Request: q))).ToList();
        var matches = all.Where(x => ShortId.For(x.Key, x.Request.Id) == term || x.Request.Id.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No package request matches '{term}'. 'elevate packages requests' shows the open ones.", ExitCodes.NotFound),
            _ => throw new CliException($"'{term}' matches several requests: " + string.Join(", ",
                matches.Select(x => $"{x.Request.PackageName} ({ShortId.For(x.Key, x.Request.Id)}, {session.TenantName(x.Key)})")), ExitCodes.NotFound),
        };
    }

    /// <summary>The single applicable policy, or the one named by <paramref name="term"/> when several apply.</summary>
    internal static PolicyRequirement ChoosePolicy(IReadOnlyList<PolicyRequirement> requirements, string? term, string packageName)
    {
        if (requirements.Count == 0)
        {
            throw new CliException($"No policy lets you request {packageName}.", ExitCodes.Failure);
        }

        if (string.IsNullOrWhiteSpace(term))
        {
            if (requirements.Count == 1)
            {
                return requirements[0];
            }

            var lines = requirements.Select(p => $"  {p.Id} — {p.DisplayName} ({(p.IsApprovalRequired ? "approval required" : "no approval")}{(p.RequiresAnswers ? ", asks questions" : string.Empty)})");
            throw new CliException($"{packageName} can be requested under several policies; pick one with --policy:\n" + string.Join("\n", lines), ExitCodes.Usage);
        }

        var matches = requirements.Where(p => p.Id.Equals(term, StringComparison.OrdinalIgnoreCase) || p.DisplayName.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = requirements.Where(p => p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No policy for {packageName} matches '{term}'.", ExitCodes.NotFound),
            _ => throw new CliException($"'{term}' matches several policies: " + string.Join(", ", matches.Select(p => $"{p.DisplayName} ({p.Id})")), ExitCodes.NotFound),
        };
    }
}
```

In `Program.cs`, register it after the approvals command: `root.Subcommands.Add(PackageCommands.Packages());`.

If `command.SetAction((parse, ct) => list.Action!.InvokeAsync(parse, ct))` does not compile against System.CommandLine 2.0.11 (the `Action` property may be typed `CommandLineAction?` with `AsynchronousCommandLineAction.InvokeAsync`), pattern-match: `((AsynchronousCommandLineAction)list.Action!).InvokeAsync(parse, ct)`. Look at how `RoleCommands.RunStatusAsync` is shared by `status` and the root in `Program.cs` and prefer that shape: extract the list body into `internal static Task<int> RunListAsync(CommandContext context, string? account, string? tenant, CancellationToken ct)` and call it from both actions.

- [ ] **Step 4: Add the resolver unit tests**

Append to `PackageSessionTests.cs` (add `using Elevate.Cli.Commands;` and `using Elevate.Cli.Infrastructure;` and `using Elevate.Cli.Selection;`):

```csharp
    [Fact]
    public void ResolvePackageMatchesShortIdThenNameThenSubstring()
    {
        using var t = new TestSession();
        var read = new TenantPackages(Home, [new AccessPackage("pkg-sandbox", "Azure Sandbox Contributor"), new AccessPackage("pkg-exchange", "Exchange Operations")], [], []);

        PackageCommands.ResolvePackage(t.Session, [read], ShortId.For(Home, "pkg-exchange")).Package.Id.Should().Be("pkg-exchange");
        PackageCommands.ResolvePackage(t.Session, [read], "pkg-sandbox").Package.Id.Should().Be("pkg-sandbox");
        PackageCommands.ResolvePackage(t.Session, [read], "exchange operations").Package.Id.Should().Be("pkg-exchange");
        PackageCommands.ResolvePackage(t.Session, [read], "sandbox").Package.Id.Should().Be("pkg-sandbox");
        var none = () => PackageCommands.ResolvePackage(t.Session, [read], "nothing");
        none.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.NotFound);
        var many = () => PackageCommands.ResolvePackage(t.Session, [read], "e");
        many.Should().Throw<CliException>().Which.Message.Should().Contain("several");
    }

    [Fact]
    public void ChoosePolicyHandlesNoneOneAndSeveral()
    {
        var eng = new PolicyRequirement("pol-eng", "Engineers", null, true, false);
        var lead = new PolicyRequirement("pol-lead", "Team leads", null, false, false);

        var none = () => PackageCommands.ChoosePolicy([], null, "P");
        none.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Failure);
        PackageCommands.ChoosePolicy([eng], null, "P").Should().Be(eng);
        var several = () => PackageCommands.ChoosePolicy([eng, lead], null, "P");
        several.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.Usage);
        PackageCommands.ChoosePolicy([eng, lead], "pol-lead", "P").Should().Be(lead);
        PackageCommands.ChoosePolicy([eng, lead], "team", "P").Should().Be(lead);
        var missing = () => PackageCommands.ChoosePolicy([eng, lead], "nope", "P");
        missing.Should().Throw<CliException>().Which.ExitCode.Should().Be(ExitCodes.NotFound);
    }
```

- [ ] **Step 5: Run the CLI suite**

Run: `cd /Users/frode.hus/pimtray && dotnet test cli/Elevate.Cli.sln 2>&1 | tail -5`
Expected: `Passed!`, no failures.

- [ ] **Step 6: Smoke the binary without an account**

```bash
cd /Users/frode.hus/pimtray && dotnet run --project cli/src/Elevate.Cli -- --data-dir /tmp/elevate-cli-smoke packages --help && dotnet run --project cli/src/Elevate.Cli -- --data-dir /tmp/elevate-cli-smoke packages list; echo "exit $?"
```

Expected: the help lists the five subcommands; `packages list` prints `No account is signed in. Run 'elevate login' first.` and exits 3.

- [ ] **Step 7: Commit**

```bash
cd /Users/frode.hus/pimtray && git add cli/src/Elevate.Cli/Commands/PackageCommands.cs cli/src/Elevate.Cli/Program.cs cli/tests/Elevate.Cli.Tests && git commit -q -m "CLI: packages list, requests, assigned, request and cancel"
```

---

### Task 10: Docs, changelog and handover notes

**Files:**
- Modify: `cli/README.md`
- Modify: `CHANGELOG.md`
- Modify: `windows/CONTINUING.md`

- [ ] **Step 1: README command table**

In `cli/README.md`, in the "Use" table, add a row after the `elevate approvals` row:

```markdown
| `elevate packages` | Access packages (entitlement management) for accounts signed in with your own or a custom registration: `list` (with the state of any pending request or delivered assignment), `requests` (open ones; `--all` adds denied, failed and cancelled with dates and the service's status), `assigned` (delivered, with expiry and policy), `request <package> --justification …` (`--policy` when several apply; packages that ask questions are handed to My Access with a link), `cancel <id>`. The first call asks for the `EntitlementMgmt-SubjectAccess.ReadWrite` permission, which needs no admin consent. |
```

In the "Sign in" table, extend the "What it can do" cell of the own-app row to end with `, access packages` and the custom row to `The same, given the same permissions; the id is remembered`. Leave the first-party rows as they are.

- [ ] **Step 2: Changelog**

Under `## [Unreleased]` → `### Added`, append:

```markdown
- Windows Core: the access package layer (scope, models, provider, diff, new-role tracker and the
  per-tenant state records) is ported to `Elevate.Core`, so `state.json` keeps one schema across
  the macOS app, the Windows app and the CLI.
- CLI: `elevate packages list|requests|assigned|request|cancel` for access packages. Tables by
  default, `--json` for scripts; requests need a justification and, when several policies apply,
  `--policy`; packages whose policy asks questions are handed to My Access with a link.
```

- [ ] **Step 3: Handover note**

In `windows/CONTINUING.md`, add a bullet in the section that lists what the Windows app still needs (find the Tasks 8–13 list): `Access packages: Core is ported (provider, diff, tracker, state records) and the CLI uses it; the WinUI window, the tenant glyph/menu item, the 15-minute/8-hour polling and the notifications are the open GitHub issue.` Keep the file's existing style (one line per bullet).

- [ ] **Step 4: Run both suites once more**

```bash
cd /Users/frode.hus/pimtray && dotnet test windows/Elevate.sln 2>&1 | tail -3 && dotnet test cli/Elevate.Cli.sln 2>&1 | tail -3
```

Expected: both `Passed!`.

- [ ] **Step 5: Commit**

```bash
cd /Users/frode.hus/pimtray && git add cli/README.md CHANGELOG.md windows/CONTINUING.md && git commit -q -m "Docs: access packages in the Core port and the CLI"
```

---

## Self-review notes

- Spec coverage: section 2 (scope, claim, tenant flag) → Task 1; section 3 models → Task 2; provider → Task 3; diff and tracker → Tasks 4–5; state records with `removeTenant` → Task 6; issue #93 commands and sign-in scope path → Tasks 7–9 (the own-app MSAL path requests scopes by name through `MsalCliProvider.Requested`, so the entitlement scope is requested on first use via `InteractionRetry`; first-party methods use `.default` and are excluded by `AccessPackagesUnavailableReason`); docs → Task 10.
- Names used across tasks: `Scopes.EntitlementAll`, `AccessPackageRequestStates.Parse/IsOpen/IsDeclined/IsCancellable`, `AccessPackageAssignmentStates.Parse`, `AccessPackageSnapshot(requests, assignments)`, `IAccessPackageProvider` members, `AccessPackageEvent.Approved/Denied/DeliveryFailed/Revoked/Expired`, `NewRoleTracker.Observe/PanelOpened/IsNew/Clone`, `AppState.AccessPackagesFor/SetAccessPackages/RoleTrackerFor/SetRoleTracker`, `ElevateSession.Packages/AccessPackagesUnavailableReason/AccessPackageTenants/ReadPackagesAsync/PackageRequirementsAsync/RequestPackageAsync/CancelPackageRequestAsync/ProbeAccessPackagesAsync`, `TenantPackages`, `ShortId.For(TenantKey, string)`, `Views.PackageState/Package/PackageRequest/PackageAssignment/PolicyOption/RequestStateMarkup/PackagesTable/PackageRequestsTable/PackageAssignmentsTable`, `PackageCommands.Packages/ResolvePackage/ResolveRequest/ChoosePolicy`.
