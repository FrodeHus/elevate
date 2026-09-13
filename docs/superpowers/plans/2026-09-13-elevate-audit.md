# Elevate Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `elevate-audit`, a separate read-only .NET CLI that signs in as an administrator, reads a tenant's Entra role assignments, group nesting, PIM for Groups and Azure RBAC, and reports every piece of standing privileged access that should become PIM eligibility, as terminal output, JSON and a site-styled HTML report.

**Architecture:** A new `audit/` solution referencing `windows/src/Elevate.Core` for the Graph/ARM transport, JSON options and role catalogue. Collectors turn HTTP pages into an immutable `Snapshot`; pure rules turn the snapshot into `Finding`s; three renderers print them. Sign-in is a small in-memory MSAL provider that uses Microsoft's Graph PowerShell public client for Graph and the Azure CLI public client for ARM, so no app registration is needed.

**Tech Stack:** .NET 10 (`global.json` SDK `10.0.100`), C# latest, System.CommandLine 2.0.11, Spectre.Console 0.57.2, Microsoft.Identity.Client 4.88.0, xunit 2.9.3 + FluentAssertions 7.1.0, static HTML/CSS for the site page, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-13-elevate-audit-design.md`

## Global Constraints

- Work on branch `audit-tool`; commit after every task with the message given in the task. Never push to `main` directly; the branch ends in a PR.
- Everything lives under `audit/` except: `site/audit.html`, `site/audit-sample.html`, `site/index.html` (two links), `docs/audit.md`, `docs/README.md`, `docs/entra-app-registration.md`, `docs/releasing.md`, `scripts/update-audit-formula.sh`, `Formula/elevate-audit.rb`, `.github/workflows/audit.yml`, `.github/workflows/release.yml`.
- **Never modify `windows/src/Elevate.Core` or `cli/`.** Missing DTOs are added to the audit project.
- Build settings are those of `cli/`: `net10.0`, `<Nullable>enable`, `<ImplicitUsings>enable`, `<TreatWarningsAsErrors>true`, `<EnableNETAnalyzers>true`, `<LangVersion>latest`. A warning is a build failure; fix it, do not suppress it globally.
- Package versions, verbatim: `Microsoft.Identity.Client` 4.88.0, `Spectre.Console` 0.57.2, `System.CommandLine` 2.0.11, `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4, `Microsoft.NET.Test.Sdk` 17.14.1, `FluentAssertions` 7.1.0, `coverlet.collector` 6.0.4.
- Executable and assembly name `elevate-audit`; root namespace `Elevate.Audit`; archives `elevate-audit-<version>-<rid>.tar.gz` (`.zip` on Windows); formula `elevate-audit`; winget id `Reothor.Elevate.Audit`.
- Client ids, verbatim: Graph default `14d82eec-204b-4c2f-b7e8-296a70dab67e` (Microsoft Graph Command Line Tools), ARM `04b07795-8ddb-461a-bbee-02f9e1bf7b46` (Azure CLI).
- Graph scopes requested with the default client, verbatim and in this order: `https://graph.microsoft.com/User.Read`, `https://graph.microsoft.com/RoleManagement.Read.Directory`, `https://graph.microsoft.com/PrivilegedAssignmentSchedule.Read.AzureADGroup`, `https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup`, `https://graph.microsoft.com/GroupMember.Read.All`, `https://graph.microsoft.com/User.ReadBasic.All`. With `--client-id`: `https://graph.microsoft.com/.default`. ARM always `https://management.azure.com/.default`.
- Every HTTP call is a GET except `directoryObjects/getByIds` (POST). Graph is `v1.0` only. Nothing is ever written to Graph or ARM. No token is written to disk.
- Exit codes: `0` no High findings, `1` error, `2` at least one High finding.
- Sample and fixture data use fictional `contoso.com` people (Alex Rivera, Sam Chen, Priya Natarajan, Jordan Lee) and fictional GUIDs. Never a real address or the maintainer's name.
- Run tests with `dotnet test audit/Elevate.Audit.sln`. Run the site validator with `python3 scripts/check-site.py`. Both must pass at the end of every task that touches their inputs.
- Test files use xunit `[Fact]`, FluentAssertions, and the `Should()` style already used in `cli/tests`.

---

## File structure

```
audit/
  global.json                         copy of cli/global.json
  Directory.Build.props               copy of cli/Directory.Build.props
  Elevate.Audit.sln
  package.sh                          publish/archive one RID (from cli/package.sh)
  README.md
  winget/New-Manifest.ps1, winget/templates/*.yaml
  src/Elevate.Audit/
    Elevate.Audit.csproj
    Program.cs                        Main, BuildRootCommand
    Infrastructure/ExitCodes.cs       ExitCodes, AuditException
    Infrastructure/AppInfo.cs         version string
    Infrastructure/RepoLocator.cs     (tests only, see tests/Support)
    Auth/ClientIds.cs                 client ids + scope lists + ScopesFor
    Auth/MsalErrors.cs                MSAL exception → PimException, user messages
    Auth/AuditTokenProvider.cs        ITokenProvider over two MSAL public clients
    Networking/RetryingHttpClient.cs  429/503 retry decorator
    Model/Snapshot.cs                 Snapshot + records + enums
    Model/Finding.cs                  Finding, Severity, sub-records
    Model/AuditJson.cs                serializer options
    Model/AuditOptions.cs             options that affect rules/output
    Collectors/GraphUrls.cs           every Graph/ARM URL in one place
    Collectors/DirectoryRoleCollector.cs
    Collectors/GroupCollector.cs
    Collectors/PrincipalCollector.cs
    Collectors/AzureCollector.cs
    Collectors/Scanner.cs             orchestrates collectors → Snapshot
    Rules/IRule.cs                    IRule, RuleContext
    Rules/GroupExpansion.cs           BFS with cycle guard → member paths
    Rules/Privilege.cs                Entra isPrivileged + Azure list
    Rules/PortalLinks.cs
    Rules/RuleRunner.cs
    Rules/EntraRules.cs               ENTRA-USER-PERMANENT, ENTRA-GROUP-PERMANENT, ENTRA-GROUP-NOT-PIM, ENTRA-GROUP-NOT-ASSIGNABLE
    Rules/GroupAndPrincipalRules.cs   GROUP-MEMBER-PERMANENT, GUEST-PERMANENT, SP-PERMANENT
    Rules/AzureAndHygieneRules.cs     AZURE-PERMANENT, ELIGIBLE-NO-END, GA-COUNT
    Resources/AzurePrivilegedRoles.json
    Rendering/AuditReport.cs          the JSON document shape
    Rendering/JsonRenderer.cs
    Rendering/SnapshotFile.cs         save/load with kind marker
    Rendering/TerminalRenderer.cs
    Rendering/HtmlRenderer.cs
    Rendering/report.css              embedded resource
    Commands/ScanCommand.cs           options, wiring, exit code
    Commands/VersionCommand.cs
    Commands/UpdateCommand.cs + Update/ReleaseChecker.cs
  tests/Elevate.Audit.Tests/
    Elevate.Audit.Tests.csproj
    Support/StubHttpClient.cs         routes by method + URL substring
    Support/SnapshotBuilder.cs        fluent fixture builder
    Support/SampleSnapshot.cs         the fictional Contoso tenant
    Support/Repo.cs                   finds the repository root
    Support/FakeTokenProvider.cs
    Fixtures/snapshots/sample.json    golden serialisation of SampleSnapshot
    *Tests.cs                          one per task
site/audit.html, site/audit-sample.html
docs/audit.md
```

---

### Task 1: Solution scaffold, `version` command, package script

**Files:**
- Create: `audit/global.json`, `audit/Directory.Build.props`, `audit/Elevate.Audit.sln`, `audit/package.sh`
- Create: `audit/src/Elevate.Audit/Elevate.Audit.csproj`, `audit/src/Elevate.Audit/Program.cs`, `audit/src/Elevate.Audit/Infrastructure/ExitCodes.cs`, `audit/src/Elevate.Audit/Infrastructure/AppInfo.cs`, `audit/src/Elevate.Audit/Commands/VersionCommand.cs`
- Create: `audit/tests/Elevate.Audit.Tests/Elevate.Audit.Tests.csproj`, `audit/tests/Elevate.Audit.Tests/CommandTreeTests.cs`

**Interfaces:**
- Produces: `Program.BuildRootCommand(): RootCommand`; `ExitCodes.Ok = 0, Failure = 1, HighFindings = 2, Interrupted = 130`; `AuditException(string message, int exitCode = 1)`; `AppInfo.Version: string`.

- [ ] **Step 1: Copy the build settings and create the solution**

```bash
cp cli/global.json audit/global.json
cp cli/Directory.Build.props audit/Directory.Build.props
mkdir -p audit/src/Elevate.Audit/{Infrastructure,Commands} audit/tests/Elevate.Audit.Tests
```

Write `audit/src/Elevate.Audit/Elevate.Audit.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>elevate-audit</AssemblyName>
    <RootNamespace>Elevate.Audit</RootNamespace>
    <!-- Overridden by the release workflow (Version=x.y.z); printed by `version`. -->
    <Version>0.0.0</Version>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <InvariantGlobalization>true</InvariantGlobalization>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <RollForward>Major</RollForward>
  </PropertyGroup>

  <!-- Same publish shape as cli/: one self-contained single file per RID, partial trimming,
       reflection-based System.Text.Json kept working. -->
  <PropertyGroup Condition="'$(_IsPublishing)' == 'true'">
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <PublishReadyToRun>true</PublishReadyToRun>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <DebugType>embedded</DebugType>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>
    <JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>
    <NoWarn>$(NoWarn);IL2026;IL2104;IL3050</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Elevate.Audit.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Identity.Client" Version="4.88.0" />
    <PackageReference Include="Spectre.Console" Version="0.57.2" />
    <PackageReference Include="System.CommandLine" Version="2.0.11" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\windows\src\Elevate.Core\Elevate.Core.csproj" />
  </ItemGroup>

</Project>
```

Write `audit/tests/Elevate.Audit.Tests/Elevate.Audit.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="FluentAssertions" Version="7.1.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="Fixtures\**\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Elevate.Audit\Elevate.Audit.csproj" />
  </ItemGroup>

</Project>
```

Then:

```bash
cd audit && dotnet new sln -n Elevate.Audit && dotnet sln add src/Elevate.Audit/Elevate.Audit.csproj tests/Elevate.Audit.Tests/Elevate.Audit.Tests.csproj ../windows/src/Elevate.Core/Elevate.Core.csproj && cd ..
```

- [ ] **Step 2: Write the failing test**

`audit/tests/Elevate.Audit.Tests/CommandTreeTests.cs`:

```csharp
using System.CommandLine;
using Elevate.Audit;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class CommandTreeTests
{
    [Fact]
    public void Root_HasVersionAndUpdateSubcommands()
    {
        var root = Program.BuildRootCommand();

        root.Subcommands.Select(c => c.Name).Should().Contain(["version"]);
    }

    [Fact]
    public async Task Version_PrintsTheToolNameAndVersion()
    {
        var stdout = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var code = await Program.BuildRootCommand().Parse(["version"]).InvokeAsync();
            code.Should().Be(0);
        }
        finally
        {
            Console.SetOut(previous);
        }

        stdout.ToString().Should().StartWith("elevate-audit ");
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build error, `Program` does not exist.

- [ ] **Step 4: Write the minimal implementation**

`audit/src/Elevate.Audit/Infrastructure/ExitCodes.cs`:

```csharp
namespace Elevate.Audit.Infrastructure;

/// <summary>Process exit codes. Pipelines branch on these; keep them stable.</summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The scan could not complete (sign-in, network, a required source refused).</summary>
    public const int Failure = 1;

    /// <summary>The scan completed and found at least one High finding.</summary>
    public const int HighFindings = 2;

    /// <summary>Interrupted with Ctrl+C.</summary>
    public const int Interrupted = 130;
}

/// <summary>An error the tool reports on stderr as one line and turns into an exit code; never a stack trace.</summary>
public sealed class AuditException(string message, int exitCode = ExitCodes.Failure) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
```

`audit/src/Elevate.Audit/Infrastructure/AppInfo.cs`:

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

namespace Elevate.Audit.Infrastructure;

public static class AppInfo
{
    public const string Name = "elevate-audit";

    /// <summary>The informational version stamped by the build (`-p:Version=x.y.z`), "0.0.0" in a dev build.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Runtime => $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}";
}
```

`audit/src/Elevate.Audit/Commands/VersionCommand.cs`:

```csharp
using System.CommandLine;
using Elevate.Audit.Infrastructure;

namespace Elevate.Audit.Commands;

public static class VersionCommand
{
    public static Command Create()
    {
        var command = new Command("version", "Print the tool version and runtime.");
        command.SetAction(_ =>
        {
            Console.Out.WriteLine($"{AppInfo.Name} {AppInfo.Version} ({AppInfo.Runtime})");
            return ExitCodes.Ok;
        });
        return command;
    }
}
```

`audit/src/Elevate.Audit/Program.cs`:

```csharp
using System.CommandLine;
using Elevate.Audit.Commands;
using Elevate.Audit.Infrastructure;
using Elevate.Core.Models;

namespace Elevate.Audit;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        TryUseUtf8();
        var parse = BuildRootCommand().Parse(args);
        var configuration = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
        try
        {
            return await parse.InvokeAsync(configuration).ConfigureAwait(false);
        }
        catch (AuditException e)
        {
            Console.Error.WriteLine(e.Message);
            return e.ExitCode;
        }
        catch (PimException e)
        {
            Console.Error.WriteLine(e.UserMessage);
            return ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Interrupted;
        }
    }

    private static void TryUseUtf8()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception e) when (e is IOException or System.Security.SecurityException)
        {
            // Redirected or restricted output; leave the encoding alone.
        }
    }

    /// <summary>The whole command tree; shared with the tests.</summary>
    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM.");
        root.Subcommands.Add(VersionCommand.Create());
        return root;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: 2 passed.

- [ ] **Step 6: Package script**

Copy `cli/package.sh` to `audit/package.sh` and change exactly these lines: `NAME="elevate-audit-$VERSION-$RID"`; `win-*) EXE="elevate-audit.exe" ;;`; `linux-*|osx-*) EXE="elevate-audit" ;;`; the `dotnet publish` path to `"$ROOT/src/Elevate.Audit/Elevate.Audit.csproj"`; the header comments to say `audit/package.sh` and `elevate-audit-<version>-<rid>`. `chmod +x audit/package.sh`. Verify:

```bash
./audit/package.sh publish 0.0.0 osx-arm64 && ./audit/dist/osx-arm64/elevate-audit version
```

Expected: `elevate-audit 0.0.0 (.NET 10.0.x, osx-arm64)`. Add `audit/dist/` to `.gitignore` if `cli/dist` is listed there (check with `grep -n dist .gitignore`); if the repo ignores `dist/` generically, nothing to add.

- [ ] **Step 7: Commit**

```bash
git add audit .gitignore
git commit -m "audit: solution scaffold, version command and package script"
```

---

### Task 2: Snapshot and Finding model

**Files:**
- Create: `audit/src/Elevate.Audit/Model/Snapshot.cs`, `audit/src/Elevate.Audit/Model/Finding.cs`, `audit/src/Elevate.Audit/Model/AuditJson.cs`, `audit/src/Elevate.Audit/Model/AuditOptions.cs`
- Test: `audit/tests/Elevate.Audit.Tests/ModelTests.cs`

**Interfaces:**
- Produces every record below. Later tasks use them by these exact names.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors for the missing types.

- [ ] **Step 3: Write the model**

`audit/src/Elevate.Audit/Model/Snapshot.cs`:

```csharp
namespace Elevate.Audit.Model;

public enum PrincipalType { User, Group, ServicePrincipal, Device, Contact, Unknown }

/// <summary>Graph's <c>assignmentType</c>: a standing assignment or a PIM activation of an eligibility.</summary>
public enum AssignmentType { Assigned, Activated }

public enum PimStatus { Onboarded, NotOnboarded, Unknown }

public enum AzureScopeKind { ManagementGroup, Subscription, ResourceGroup, Resource }

public sealed record TenantInfo(string Id, string? DisplayName);

public sealed record PrincipalRecord(
    string Id,
    PrincipalType Type,
    string? DisplayName,
    string? UserPrincipalName,
    bool IsGuest,
    bool? AccountEnabled,
    string? ServicePrincipalType);

public sealed record RoleDefinitionRecord(string Id, string? TemplateId, string DisplayName, bool IsPrivileged, bool IsBuiltIn);

/// <summary>One Entra role assignment or eligibility schedule instance. <see cref="AssignmentType"/> is null for eligibilities.</summary>
public sealed record EntraAssignmentRecord(
    string Id,
    string PrincipalId,
    string RoleDefinitionId,
    string DirectoryScopeId,
    string? AppScopeId,
    AssignmentType? AssignmentType,
    string? MemberType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime)
{
    public bool IsPermanent => AssignmentType == Model.AssignmentType.Assigned && EndDateTime is null;
}

public sealed record GroupMemberRecord(string Id, PrincipalType Type);

/// <summary>One PIM for Groups schedule instance; <see cref="AccessId"/> is "member" or "owner".</summary>
public sealed record GroupPimRecord(
    string Id,
    string PrincipalId,
    string AccessId,
    AssignmentType? AssignmentType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime)
{
    public bool IsPermanent => AssignmentType == Model.AssignmentType.Assigned && EndDateTime is null;
}

public sealed record GroupRecord(
    string Id,
    string DisplayName,
    bool IsAssignableToRole,
    bool IsDynamic,
    PimStatus PimStatus,
    IReadOnlyList<GroupMemberRecord> DirectMembers,
    IReadOnlyList<GroupPimRecord> PimAssignments,
    IReadOnlyList<GroupPimRecord> PimEligibilities);

public sealed record AzureScopeRecord(string Id, AzureScopeKind Kind, string DisplayName);

public sealed record AzureRoleDefinitionRecord(string Id, string Name, string DisplayName, string Type, IReadOnlyList<string> Actions);

/// <summary>
/// One Azure role assignment. <see cref="FromSchedule"/> is true for PIM schedule instances (which carry
/// <see cref="AssignmentType"/>) and false for classic <c>roleAssignments</c> entries.
/// </summary>
public sealed record AzureAssignmentRecord(
    string Id,
    string Scope,
    string RoleDefinitionId,
    string PrincipalId,
    string? PrincipalType,
    AssignmentType? AssignmentType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    bool FromSchedule);

public sealed record SkippedSource(string Source, string Reason);

/// <summary>Everything one scan read. Immutable; the rules see nothing else.</summary>
public sealed record Snapshot(
    string Kind,
    string ToolVersion,
    TenantInfo Tenant,
    string Account,
    DateTimeOffset ScannedAt,
    IReadOnlyList<RoleDefinitionRecord> EntraRoleDefinitions,
    IReadOnlyList<EntraAssignmentRecord> EntraAssignments,
    IReadOnlyList<EntraAssignmentRecord> EntraEligibilities,
    IReadOnlyList<GroupRecord> Groups,
    IReadOnlyList<PrincipalRecord> Principals,
    IReadOnlyList<AzureScopeRecord> AzureScopes,
    IReadOnlyList<AzureRoleDefinitionRecord> AzureRoleDefinitions,
    IReadOnlyList<AzureAssignmentRecord> AzureAssignments,
    IReadOnlyList<AzureAssignmentRecord> AzureEligibilities,
    IReadOnlyList<SkippedSource> Skipped)
{
    public const string KindMarker = "elevate-audit-snapshot";

    public static Snapshot Empty(TenantInfo tenant, string account, DateTimeOffset scannedAt, string toolVersion = "0.0.0") =>
        new(KindMarker, toolVersion, tenant, account, scannedAt, [], [], [], [], [], [], [], [], [], []);
}
```

`audit/src/Elevate.Audit/Model/Finding.cs`:

```csharp
namespace Elevate.Audit.Model;

public enum Severity { Info, Low, Medium, High }

public static class Severities
{
    /// <summary>Sort key: High first.</summary>
    public static int Rank(Severity severity) => severity switch
    {
        Severity.High => 0,
        Severity.Medium => 1,
        Severity.Low => 2,
        _ => 3,
    };

    public static bool TryParse(string? text, out Severity severity) => Enum.TryParse(text, ignoreCase: true, out severity) && Enum.IsDefined(severity);

    public static Severity Parse(string text) => TryParse(text, out var s) ? s : throw new ArgumentException($"Unknown severity '{text}'. Use high, medium, low or info.", nameof(text));
}

public enum RoleSystem { Entra, Azure, Group }

public enum ScopeKind { Directory, AdministrativeUnit, Application, ManagementGroup, Subscription, ResourceGroup, Resource, Group }

public sealed record FindingPrincipal(string Id, string DisplayName, string? UserPrincipalName, PrincipalType Type, bool IsGuest, bool? AccountEnabled);

public sealed record FindingRole(string Id, string DisplayName, string? TemplateId, bool IsPrivileged, RoleSystem System);

public sealed record FindingScope(string Id, string DisplayName, ScopeKind Kind);

public sealed record GroupRef(string Id, string DisplayName);

public sealed record FindingEvidence(string AssignmentId, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime, AssignmentType? AssignmentType, string? MemberType);

public sealed record Finding(
    string Id,
    Severity Severity,
    FindingPrincipal Principal,
    FindingRole Role,
    FindingScope Scope,
    IReadOnlyList<GroupRef> Via,
    string Remedy,
    string PortalUrl,
    FindingEvidence Evidence);
```

`audit/src/Elevate.Audit/Model/AuditJson.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elevate.Audit.Model;

/// <summary>Serializer options for snapshots and reports: camelCase, string enums, nulls omitted, indented.</summary>
public static class AuditJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
```

`audit/src/Elevate.Audit/Model/AuditOptions.cs`:

```csharp
namespace Elevate.Audit.Model;

/// <summary>The command-line choices the rules and renderers need to know about.</summary>
public sealed record AuditOptions(
    bool AllRoles = false,
    IReadOnlyList<string>? Ignored = null,
    Severity MinSeverity = Severity.Info,
    bool SkipAzure = false)
{
    public IReadOnlyList<string> IgnoredRules => Ignored ?? [];
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: 5 passed. If `BeEquivalentTo` complains about the computed `IsPermanent` property, exclude it: `.Excluding(ctx => ctx.Path.EndsWith("IsPermanent"))` is acceptable.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: snapshot and finding model"
```

---

### Task 3: Retrying HTTP client and the test stub

**Files:**
- Create: `audit/src/Elevate.Audit/Networking/RetryingHttpClient.cs`
- Create: `audit/tests/Elevate.Audit.Tests/Support/StubHttpClient.cs`, `audit/tests/Elevate.Audit.Tests/RetryingHttpClientTests.cs`

**Interfaces:**
- Consumes: `Elevate.Core.Networking.IHttpClient`, `HttpRequestData`, `HttpResponseData`.
- Produces: `RetryingHttpClient(IHttpClient inner, Func<TimeSpan, CancellationToken, Task>? delay = null, int maxAttempts = 5)`; test `StubHttpClient` with `On(string method, string urlContains, string body, int status = 200)`, `On(string method, string urlContains, Func<HttpRequestData, HttpResponseData> respond)`, `Requests`, `RequestsMatching(string)`.

- [ ] **Step 1: Write the stub and the failing test**

`audit/tests/Elevate.Audit.Tests/Support/StubHttpClient.cs`:

```csharp
using System.Text;
using Elevate.Core.Networking;

namespace Elevate.Audit.Tests.Support;

/// <summary>Routes requests by HTTP method plus a URL substring; the last matching registration wins. Records every request.</summary>
public sealed class StubHttpClient : IHttpClient
{
    private sealed record Route(string Method, string UrlContains, Func<HttpRequestData, HttpResponseData> Respond);

    private readonly List<Route> _routes = [];
    private readonly List<HttpRequestData> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<HttpRequestData> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    public IReadOnlyList<HttpRequestData> RequestsMatching(string urlContains) =>
        Requests.Where(r => r.Url.AbsoluteUri.Contains(urlContains, StringComparison.Ordinal)).ToList();

    public void On(string method, string urlContains, string body, int status = 200) =>
        On(method, urlContains, _ => new HttpResponseData(status, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, Encoding.UTF8.GetBytes(body)));

    public void On(string method, string urlContains, Func<HttpRequestData, HttpResponseData> respond)
    {
        lock (_gate) { _routes.Add(new Route(method, urlContains, respond)); }
    }

    public Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        lock (_gate)
        {
            _requests.Add(request);
            var route = _routes.LastOrDefault(r =>
                string.Equals(r.Method, request.Method, StringComparison.OrdinalIgnoreCase)
                && request.Url.AbsoluteUri.Contains(r.UrlContains, StringComparison.Ordinal));
            return Task.FromResult(route?.Respond(request)
                ?? new HttpResponseData(599, new Dictionary<string, string>(), Encoding.UTF8.GetBytes($"no stub for {request.Method} {request.Url}")));
        }
    }
}
```

`audit/tests/Elevate.Audit.Tests/RetryingHttpClientTests.cs`:

```csharp
using System.Text;
using Elevate.Audit.Networking;
using Elevate.Audit.Tests.Support;
using Elevate.Core.Networking;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class RetryingHttpClientTests
{
    private static HttpRequestData Get(string url) => new("GET", new Uri(url));

    [Fact]
    public async Task Retries429_HonouringRetryAfter()
    {
        var stub = new StubHttpClient();
        var calls = 0;
        stub.On("GET", "/users", _ => ++calls == 1
            ? new HttpResponseData(429, new Dictionary<string, string> { ["Retry-After"] = "3" }, Encoding.UTF8.GetBytes("slow down"))
            : new HttpResponseData(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")));
        var waits = new List<TimeSpan>();
        var client = new RetryingHttpClient(stub, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        var response = await client.SendAsync(Get("https://graph.microsoft.com/v1.0/users"), CancellationToken.None);

        response.Status.Should().Be(200);
        stub.Requests.Should().HaveCount(2);
        waits.Should().Equal(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts_AndReturnsTheLastResponse()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/users", _ => new HttpResponseData(503, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("busy")));
        var client = new RetryingHttpClient(stub, (_, _) => Task.CompletedTask, maxAttempts: 3);

        var response = await client.SendAsync(Get("https://graph.microsoft.com/v1.0/users"), CancellationToken.None);

        response.Status.Should().Be(503);
        stub.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task DoesNotRetryOtherStatuses_AndCapsTheWait()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/forbidden", "nope", 403);
        stub.On("GET", "/slow", _ => new HttpResponseData(429, new Dictionary<string, string> { ["Retry-After"] = "600" }, []));
        var waits = new List<TimeSpan>();
        var client = new RetryingHttpClient(stub, (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, maxAttempts: 2);

        (await client.SendAsync(Get("https://graph.microsoft.com/v1.0/forbidden"), CancellationToken.None)).Status.Should().Be(403);
        await client.SendAsync(Get("https://graph.microsoft.com/v1.0/slow"), CancellationToken.None);

        stub.RequestsMatching("/forbidden").Should().HaveCount(1);
        waits.Should().Equal(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task MissingRetryAfter_BacksOffExponentially()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/users", _ => new HttpResponseData(429, new Dictionary<string, string>(), []));
        var waits = new List<TimeSpan>();
        var client = new RetryingHttpClient(stub, (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, maxAttempts: 4);

        await client.SendAsync(Get("https://graph.microsoft.com/v1.0/users"), CancellationToken.None);

        waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build error, `RetryingHttpClient` missing.

- [ ] **Step 3: Implement**

`audit/src/Elevate.Audit/Networking/RetryingHttpClient.cs`:

```csharp
using System.Globalization;
using Elevate.Core.Networking;

namespace Elevate.Audit.Networking;

/// <summary>
/// Retries 429 and 503 replies, waiting for <c>Retry-After</c> (seconds or an HTTP date), capped at
/// 60 s, or 2, 4, 8… seconds when the header is absent. Core's transport maps a 429 straight to an
/// error, and a tenant-wide scan hits the Graph throttle routinely, so this sits under it.
/// </summary>
public sealed class RetryingHttpClient(
    IHttpClient inner,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    int maxAttempts = 5) : IHttpClient
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        HttpResponseData response;
        var attempt = 0;
        while (true)
        {
            attempt++;
            response = await inner.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Status is not (429 or 503) || attempt >= maxAttempts)
            {
                return response;
            }

            await _delay(WaitFor(response, attempt), ct).ConfigureAwait(false);
        }
    }

    internal static TimeSpan WaitFor(HttpResponseData response, int attempt)
    {
        var wait = response.Header("Retry-After") switch
        {
            { } seconds when double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => TimeSpan.FromSeconds(Math.Max(0, s)),
            { } date when DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) => at - DateTimeOffset.UtcNow,
            _ => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
        };
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        return wait > MaxWait ? MaxWait : wait;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: retrying HTTP client for 429 and 503"
```

---

### Task 4: Client ids, scope selection and the MSAL token provider

**Files:**
- Create: `audit/src/Elevate.Audit/Auth/ClientIds.cs`, `audit/src/Elevate.Audit/Auth/MsalErrors.cs`, `audit/src/Elevate.Audit/Auth/AuditTokenProvider.cs`
- Test: `audit/tests/Elevate.Audit.Tests/AuthTests.cs`

**Interfaces:**
- Consumes: `Elevate.Core.Auth.ITokenProvider`, `Elevate.Core.Models.Identity`, `SignInMethod`, `PimException`, `PimErrorKind`.
- Produces: `ClientIds.GraphDefault`, `ClientIds.AzureCli`, `ClientIds.GraphReadScopes`, `ClientIds.GraphDefaultScope`, `ClientIds.ArmDefaultScope`; `enum Resource { Graph, Arm }`; `ClientIds.ScopesFor(IReadOnlyList<string> requested, bool customGraphClient): (Resource Resource, IReadOnlyList<string> Scopes)`; `MsalErrors.Map(Exception): PimException`; `MsalErrors.Explain(PimException error, string graphClientId): string`; `AuditTokenProvider(string? graphClientId, string tenant, bool deviceCode, Action<string> say)` implementing `ITokenProvider`, plus `Task<Identity> SignInAsync(CancellationToken)` and `string GraphClientId`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Elevate.Audit.Auth;
using Elevate.Core.Models;
using FluentAssertions;
using Microsoft.Identity.Client;

namespace Elevate.Audit.Tests;

public class AuthTests
{
    [Fact]
    public void ScopesFor_GraphWithDefaultClient_AlwaysAsksForTheSixReadScopes()
    {
        var (resource, scopes) = ClientIds.ScopesFor(["https://graph.microsoft.com/User.Read"], customGraphClient: false);

        resource.Should().Be(Resource.Graph);
        scopes.Should().Equal(ClientIds.GraphReadScopes);
        scopes.Should().HaveCount(6).And.StartWith("https://graph.microsoft.com/User.Read");
    }

    [Fact]
    public void ScopesFor_GraphWithCustomClient_AsksForDefault()
    {
        var (_, scopes) = ClientIds.ScopesFor(ClientIds.GraphReadScopes, customGraphClient: true);

        scopes.Should().Equal("https://graph.microsoft.com/.default");
    }

    [Fact]
    public void ScopesFor_Arm_AlwaysAsksForDefault()
    {
        var (resource, scopes) = ClientIds.ScopesFor(["https://management.azure.com/user_impersonation"], customGraphClient: false);

        resource.Should().Be(Resource.Arm);
        scopes.Should().Equal("https://management.azure.com/.default");
    }

    [Fact]
    public void Map_ConsentDeclined_IsConsentRequired()
    {
        var error = MsalErrors.Map(new MsalServiceException("access_denied", "AADSTS65004: User declined to consent to access the app."));

        error.Kind.Should().Be(PimErrorKind.ConsentRequired);
    }

    [Fact]
    public void Map_BlockedApp_IsForbiddenWithTheCodeKept()
    {
        var error = MsalErrors.Map(new MsalServiceException("invalid_client", "AADSTS7000112: Application 'x' is disabled."));

        error.Kind.Should().Be(PimErrorKind.Forbidden);
        error.Detail.Should().Contain("AADSTS7000112");
    }

    [Fact]
    public void Explain_ConsentRequired_PointsAtClientIdOverride()
    {
        var text = MsalErrors.Explain(new PimException(PimErrorKind.ConsentRequired), ClientIds.GraphDefault);

        text.Should().Contain("Microsoft Graph Command Line Tools").And.Contain("--client-id").And.Contain("docs/audit.md");
    }

    [Fact]
    public void Explain_Forbidden_NamesTheRolesThatCanRead()
    {
        var text = MsalErrors.Explain(new PimException(PimErrorKind.Forbidden, "Authorization failed"), ClientIds.GraphDefault);

        text.Should().Contain("Global Reader").And.Contain("Privileged Role Administrator");
    }

    [Fact]
    public void Provider_RejectsANonGuidClientId()
    {
        var act = () => new AuditTokenProvider("not-a-guid", "organizations", deviceCode: false, _ => { });

        act.Should().Throw<Elevate.Audit.Infrastructure.AuditException>().WithMessage("*client*GUID*");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

`audit/src/Elevate.Audit/Auth/ClientIds.cs`:

```csharp
namespace Elevate.Audit.Auth;

public enum Resource { Graph, Arm }

/// <summary>The two Microsoft public clients the auditor signs in with, and what it asks each for.</summary>
public static class ClientIds
{
    /// <summary>"Microsoft Graph Command Line Tools": first-party, multi-tenant, dynamic consent, loopback and device-code redirects.</summary>
    public const string GraphDefault = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public const string GraphDefaultDisplayName = "Microsoft Graph Command Line Tools";

    /// <summary>The Azure CLI's client, pre-consented for Azure Resource Manager in every tenant.</summary>
    public const string AzureCli = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    public const string GraphDefaultScope = "https://graph.microsoft.com/.default";
    public const string ArmDefaultScope = "https://management.azure.com/.default";

    /// <summary>Read-only, in the order shown to the consenting administrator.</summary>
    public static IReadOnlyList<string> GraphReadScopes { get; } =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/RoleManagement.Read.Directory",
        "https://graph.microsoft.com/PrivilegedAssignmentSchedule.Read.AzureADGroup",
        "https://graph.microsoft.com/PrivilegedEligibilitySchedule.Read.AzureADGroup",
        "https://graph.microsoft.com/GroupMember.Read.All",
        "https://graph.microsoft.com/User.ReadBasic.All",
    ];

    /// <summary>The bare scope names, for docs and the report appendix.</summary>
    public static IReadOnlyList<string> GraphReadScopeNames { get; } = GraphReadScopes.Select(s => s["https://graph.microsoft.com/".Length..]).ToList();

    /// <summary>
    /// Which client a request belongs to and what to ask MSAL for. Graph with the default client asks for
    /// the named read scopes every time so consent happens once, up front; a custom client and ARM ask for
    /// <c>.default</c>, since what they may do is whatever was consented to them.
    /// </summary>
    public static (Resource Resource, IReadOnlyList<string> Scopes) ScopesFor(IReadOnlyList<string> requested, bool customGraphClient)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var isArm = requested.Any(s => s.StartsWith("https://management.azure.com/", StringComparison.OrdinalIgnoreCase));
        if (isArm)
        {
            return (Resource.Arm, [ArmDefaultScope]);
        }

        return (Resource.Graph, customGraphClient ? [GraphDefaultScope] : GraphReadScopes);
    }

    public static bool IsValidClientId(string? value) => Guid.TryParse((value ?? string.Empty).Trim(), out var guid) && guid != Guid.Empty;
}
```

`audit/src/Elevate.Audit/Auth/MsalErrors.cs`:

```csharp
using Elevate.Core.Models;
using Microsoft.Identity.Client;

namespace Elevate.Audit.Auth;

public static class MsalErrors
{
    /// <summary>MSAL exceptions → the Core error kinds, so the rest of the tool speaks one language.</summary>
    public static PimException Map(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case PimException pim:
                return pim;
            case MsalUiRequiredException:
                return new PimException(PimErrorKind.InteractionRequired);
            case MsalClientException client when client.ErrorCode == MsalError.AuthenticationCanceledError:
                return new PimException(PimErrorKind.SignInDeclined, "Sign-in cancelled");
            case MsalServiceException service:
            {
                var text = service.Message ?? string.Empty;
                if (text.Contains("AADSTS65001", StringComparison.Ordinal)
                    || text.Contains("AADSTS65004", StringComparison.Ordinal)
                    || text.Contains("AADSTS90094", StringComparison.Ordinal)
                    || text.Contains("consent_required", StringComparison.Ordinal))
                {
                    return new PimException(PimErrorKind.ConsentRequired, text);
                }

                if (text.Contains("AADSTS7000112", StringComparison.Ordinal)
                    || text.Contains("AADSTS700016", StringComparison.Ordinal)
                    || text.Contains("AADSTS53003", StringComparison.Ordinal)
                    || text.Contains("AADSTS500011", StringComparison.Ordinal))
                {
                    return new PimException(PimErrorKind.Forbidden, text);
                }

                return new PimException(PimErrorKind.Unexpected, text, service.StatusCode);
            }

            case MsalException msal:
                return new PimException(PimErrorKind.Unexpected, msal.Message);
            default:
                return new PimException(PimErrorKind.Network, error.Message);
        }
    }

    /// <summary>One paragraph for stderr telling the administrator what to do about a refused sign-in or read.</summary>
    public static string Explain(PimException error, string graphClientId)
    {
        ArgumentNullException.ThrowIfNull(error);
        var app = graphClientId == ClientIds.GraphDefault ? $"the {ClientIds.GraphDefaultDisplayName} app ({ClientIds.GraphDefault})" : $"client id {graphClientId}";
        var guide = "See docs/audit.md in the Elevate repository.";
        return error.Kind switch
        {
            PimErrorKind.ConsentRequired =>
                $"Consent for {app} was declined or is not permitted in this tenant. elevate-audit asks only for read scopes "
                + $"({string.Join(", ", ClientIds.GraphReadScopeNames)}). A tenant that blocks that app can pass any public client "
                + "of its own with --client-id after granting it the same read scopes. " + guide,
            PimErrorKind.Forbidden when error.Detail is { } detail && detail.Contains("AADSTS", StringComparison.Ordinal) =>
                $"Sign-in with {app} is blocked by this tenant's policy: {detail} Use --client-id with a registration your tenant allows. " + guide,
            PimErrorKind.Forbidden =>
                "The signed-in account may not list the tenant's role assignments. The scan needs a directory role that can read "
                + "PIM: Global Reader, Privileged Role Administrator or Security Reader (and Reader on the Azure scopes to audit). "
                + $"Details: {error.Detail ?? error.UserMessage}",
            PimErrorKind.SignInDeclined => "Sign-in was cancelled.",
            _ => error.UserMessage,
        };
    }
}
```

`audit/src/Elevate.Audit/Auth/AuditTokenProvider.cs`:

```csharp
using System.Security.Claims;
using Elevate.Audit.Infrastructure;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Microsoft.Identity.Client;

namespace Elevate.Audit.Auth;

/// <summary>
/// Two MSAL public clients, one per resource, with an in-memory token cache only: a scan signs in,
/// runs, and exits. Graph uses <see cref="ClientIds.GraphDefault"/> (or <c>--client-id</c>); ARM uses
/// the Azure CLI client, as Elevate's Azure CLI sign-in method does. The Identity and tenant the
/// transport passes are ignored: this provider knows one account and one tenant.
/// </summary>
public sealed class AuditTokenProvider : ITokenProvider
{
    private readonly IPublicClientApplication _graph;
    private readonly IPublicClientApplication _arm;
    private readonly bool _customGraph;
    private readonly bool _deviceCode;
    private readonly Action<string> _say;
    private readonly SemaphoreSlim _interactive = new(1, 1);
    private IAccount? _graphAccount;
    private IAccount? _armAccount;

    public AuditTokenProvider(string? graphClientId, string tenant, bool deviceCode, Action<string> say)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentNullException.ThrowIfNull(say);
        if (graphClientId is not null && !ClientIds.IsValidClientId(graphClientId))
        {
            throw new AuditException("The --client-id value must be a GUID (the application/client id of a public client registration).");
        }

        GraphClientId = graphClientId?.Trim() ?? ClientIds.GraphDefault;
        _customGraph = GraphClientId != ClientIds.GraphDefault;
        Tenant = tenant;
        _deviceCode = deviceCode;
        _say = say;
        _graph = Build(GraphClientId, tenant);
        _arm = Build(ClientIds.AzureCli, tenant);
    }

    public string GraphClientId { get; }

    public string Tenant { get; }

    /// <summary>The interactive Graph sign-in that starts a scan; consent for the read scopes happens here.</summary>
    public async Task<Identity> SignInAsync(CancellationToken ct)
    {
        var result = await InteractiveAsync(Resource.Graph, ClientIds.ScopesFor(ClientIds.GraphReadScopes, _customGraph).Scopes, ct).ConfigureAwait(false);
        _graphAccount = result.Account;
        return IdentityFrom(result);
    }

    Task<Identity> ITokenProvider.SignInAsync(SignInMethod method, CancellationToken ct) => SignInAsync(ct);

    public Task SignOutAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>([]);

    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default) =>
        AcquireAsync(scopes, ct);

    public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct = default) =>
        AcquireAsync(scopes, ct);

    private async Task<string> AcquireAsync(IReadOnlyList<string> requested, CancellationToken ct)
    {
        var (resource, scopes) = ClientIds.ScopesFor(requested, _customGraph);
        var app = resource == Resource.Arm ? _arm : _graph;
        var account = resource == Resource.Arm ? _armAccount : _graphAccount;
        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct).ConfigureAwait(false);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Fall through to an interactive acquisition.
            }
            catch (MsalException e)
            {
                throw MsalErrors.Map(e);
            }
        }

        var result = await InteractiveAsync(resource, scopes, ct).ConfigureAwait(false);
        if (resource == Resource.Arm)
        {
            _armAccount = result.Account;
        }
        else
        {
            _graphAccount = result.Account;
        }

        return result.AccessToken;
    }

    private async Task<AuthenticationResult> InteractiveAsync(Resource resource, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var app = resource == Resource.Arm ? _arm : _graph;
        var hint = _graphAccount?.Username;
        await _interactive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var what = resource == Resource.Arm ? "Azure Resource Manager" : "Microsoft Graph";
            if (_deviceCode)
            {
                var device = app.AcquireTokenWithDeviceCode(scopes, callback =>
                {
                    _say($"{what}: {callback.Message}");
                    return Task.CompletedTask;
                });
                return await device.ExecuteAsync(ct).ConfigureAwait(false);
            }

            _say($"Opening the browser to sign in to {what}{(hint is null ? string.Empty : $" as {hint}")}…");
            var builder = app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(hint is null ? Prompt.SelectAccount : Prompt.NoPrompt);
            if (hint is not null)
            {
                builder = builder.WithLoginHint(hint);
            }

            return await builder.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw MsalErrors.Map(e);
        }
        finally
        {
            _interactive.Release();
        }
    }

    private Identity IdentityFrom(AuthenticationResult result)
    {
        var claims = result.ClaimsPrincipal;
        var name = claims?.FindFirst("name")?.Value;
        var tenant = result.TenantId ?? claims?.FindFirst("tid")?.Value ?? string.Empty;
        return new Identity(
            result.Account.HomeAccountId?.Identifier ?? result.Account.Username,
            result.Account.Username,
            name ?? result.Account.Username,
            tenant,
            SignInMethod.Custom(GraphClientId));
    }

    private static IPublicClientApplication Build(string clientId, string tenant)
    {
        try
        {
            return PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, tenant)
                .WithRedirectUri("http://localhost")
                .Build();
        }
        catch (MsalException e)
        {
            throw MsalErrors.Map(e);
        }
    }
}
```

Note `SignInMethod.Custom(GraphClientId)` is deliberate: Core's `GraphTransport` turns a 403 into a plain `Forbidden` (not "grant consent to Elevate") for any non-OwnApp method, which is the right message here.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: client ids, scope selection and in-memory MSAL provider"
```

---

### Task 5: URLs, wire mapping and the directory role collector

**Files:**
- Create: `audit/src/Elevate.Audit/Collectors/GraphUrls.cs`, `audit/src/Elevate.Audit/Collectors/Wire.cs`, `audit/src/Elevate.Audit/Collectors/DirectoryRoleCollector.cs`
- Create: `audit/tests/Elevate.Audit.Tests/Support/FakeTokenProvider.cs`, `audit/tests/Elevate.Audit.Tests/Support/TestIdentity.cs`, `audit/tests/Elevate.Audit.Tests/DirectoryRoleCollectorTests.cs`

**Interfaces:**
- Consumes: `GraphTransport` (`ListAllAsync<T>`, `GetAsync`, `PostAsync`, `GraphUrl`), `GraphJson.Options`, `RoleCatalogue.EntraBuiltInRoles()`, `ClientIds.GraphReadScopes`.
- Produces: `GraphUrls` (every URL, see code), `Wire.WirePrincipal`, `Wire.PrincipalTypeOf(string?)`, `Wire.AssignmentTypeOf(string?)`, `Wire.ToRecord(WirePrincipal)`; `DirectoryRoleCollector(GraphTransport graph, Identity identity, string tenantId)` with `Task<DirectoryRoleData> CollectAsync(CancellationToken)`; `DirectoryRoleData(Definitions, Assignments, Eligibilities, Principals)`.

- [ ] **Step 1: Test support**

`audit/tests/Elevate.Audit.Tests/Support/FakeTokenProvider.cs`:

```csharp
using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.Audit.Tests.Support;

/// <summary>Hands out a fixed bearer token; the stub HTTP client never checks it.</summary>
public sealed class FakeTokenProvider : ITokenProvider
{
    public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct = default) => Task.FromResult(TestIdentity.Alex);
    public Task SignOutAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>([TestIdentity.Alex]);
    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default) => Task.FromResult("token");
    public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct = default) => Task.FromResult("token");
}
```

`audit/tests/Elevate.Audit.Tests/Support/TestIdentity.cs`:

```csharp
using Elevate.Audit.Auth;
using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Audit.Tests.Support;

public static class TestIdentity
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";

    public static readonly Identity Alex = new("home-1", "alex.rivera@contoso.com", "Alex Rivera", TenantId, SignInMethod.Custom(ClientIds.GraphDefault));

    public static GraphTransport Graph(StubHttpClient stub) => new(stub, new FakeTokenProvider());

    public static GraphTransport Arm(StubHttpClient stub) => new(stub, new FakeTokenProvider(), GraphTransport.MapArmError);
}
```

- [ ] **Step 2: Write the failing test**

`audit/tests/Elevate.Audit.Tests/DirectoryRoleCollectorTests.cs`:

```csharp
using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class DirectoryRoleCollectorTests
{
    private const string GlobalAdminTemplate = "62e90394-69f5-4237-9190-012177145e10";

    [Fact]
    public async Task Collect_FollowsPaging_MapsInstances_AndFallsBackToTheCatalogue()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/roleManagement/directory/roleDefinitions", $$"""
            {"value":[
              {"id":"rd-ga","templateId":"{{GlobalAdminTemplate}}","displayName":"Global Administrator","isBuiltIn":true},
              {"id":"rd-reader","templateId":"f2ef992c-3afb-46b9-b7cf-a126ee74c451","displayName":"Global Reader","isPrivileged":false,"isBuiltIn":true}
            ]}
            """);
        stub.On("GET", "roleAssignmentScheduleInstances?$skiptoken=page2", """
            {"value":[
              {"id":"a2","principalId":"g1","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Assigned","memberType":"Direct",
               "startDateTime":"2025-01-01T00:00:00Z","endDateTime":null,
               "principal":{"@odata.type":"#microsoft.graph.group","id":"g1","displayName":"Tier 0 Admins"}}
            ]}
            """);
        stub.On("GET", "roleAssignmentScheduleInstances?$expand", """
            {"@odata.nextLink":"https://graph.microsoft.com/v1.0/roleManagement/directory/roleAssignmentScheduleInstances?$skiptoken=page2",
             "value":[
              {"id":"a1","principalId":"u1","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Assigned","memberType":"Direct",
               "startDateTime":"2025-01-01T00:00:00Z","endDateTime":null,
               "principal":{"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Sam Chen","userPrincipalName":"sam.chen@contoso.com","userType":"Member","accountEnabled":true}},
              {"id":"a3","principalId":"u2","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Activated","memberType":"Direct",
               "startDateTime":"2026-09-13T08:00:00Z","endDateTime":"2026-09-13T16:00:00Z",
               "principal":{"@odata.type":"#microsoft.graph.user","id":"u2","displayName":"Priya Natarajan","userPrincipalName":"priya.natarajan_fabrikam.com#EXT#@contoso.com","userType":"Guest"}}
            ]}
            """);
        stub.On("GET", "roleEligibilityScheduleInstances", """
            {"value":[
              {"id":"e1","principalId":"u2","roleDefinitionId":"rd-ga","directoryScopeId":"/","memberType":"Direct","startDateTime":"2025-01-01T00:00:00Z","endDateTime":null,
               "principal":{"@odata.type":"#microsoft.graph.user","id":"u2","displayName":"Priya Natarajan","userType":"Guest"}}
            ]}
            """);

        var data = await new DirectoryRoleCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(CancellationToken.None);

        data.Definitions.Should().ContainSingle(d => d.Id == "rd-ga").Which.IsPrivileged.Should().BeTrue("the catalogue marks Global Administrator privileged when Graph omits the flag");
        data.Definitions.Should().ContainSingle(d => d.Id == "rd-reader").Which.IsPrivileged.Should().BeFalse();
        data.Assignments.Select(a => a.Id).Should().Equal("a1", "a3", "a2");
        data.Assignments.Single(a => a.Id == "a1").IsPermanent.Should().BeTrue();
        data.Assignments.Single(a => a.Id == "a3").IsPermanent.Should().BeFalse();
        data.Assignments.Single(a => a.Id == "a3").AssignmentType.Should().Be(AssignmentType.Activated);
        data.Eligibilities.Should().ContainSingle().Which.AssignmentType.Should().BeNull();
        data.Principals.Should().HaveCount(3);
        data.Principals.Single(p => p.Id == "u2").Should().BeEquivalentTo(new { Type = PrincipalType.User, IsGuest = true, DisplayName = "Priya Natarajan" });
        data.Principals.Single(p => p.Id == "g1").Type.Should().Be(PrincipalType.Group);
        stub.RequestsMatching("roleAssignmentScheduleInstances").Should().HaveCount(2);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 4: Implement**

`audit/src/Elevate.Audit/Collectors/GraphUrls.cs`:

```csharp
using Elevate.Core.Providers;

namespace Elevate.Audit.Collectors;

/// <summary>Every URL the auditor calls, in one place, so the docs' list of reads can be checked against it.</summary>
public static class GraphUrls
{
    public const string ArmBase = "https://management.azure.com";

    public static Uri Organization => Graph("/organization?$select=id,displayName");

    public static Uri RoleDefinitions => Graph("/roleManagement/directory/roleDefinitions?$select=id,templateId,displayName,isPrivileged,isBuiltIn");

    public static Uri RoleAssignmentInstances => Graph("/roleManagement/directory/roleAssignmentScheduleInstances?$expand=principal,roleDefinition");

    public static Uri RoleEligibilityInstances => Graph("/roleManagement/directory/roleEligibilityScheduleInstances?$expand=principal,roleDefinition");

    public static Uri RoleAssignableGroups => Graph("/groups?$filter=isAssignableToRole eq true&$select=id,displayName,isAssignableToRole,groupTypes");

    public static Uri Group(string id) => Graph($"/groups/{Escape(id)}?$select=id,displayName,isAssignableToRole,groupTypes");

    public static Uri GroupMembers(string id) => Graph($"/groups/{Escape(id)}/members?$top=999");

    public static Uri GroupPimAssignments(string id) => Graph($"/identityGovernance/privilegedAccess/group/assignmentScheduleInstances?$filter=groupId eq '{GraphTransport.OdataEscaped(id)}'");

    public static Uri GroupPimEligibilities(string id) => Graph($"/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances?$filter=groupId eq '{GraphTransport.OdataEscaped(id)}'");

    public static Uri GetByIds => Graph("/directoryObjects/getByIds");

    public static Uri ManagementGroups => Arm("/providers/Microsoft.Management/managementGroups", "2021-04-01");

    public static Uri Subscriptions => Arm("/subscriptions", "2022-12-01");

    /// <summary>All assignments at the subscription, its children, and inherited from above.</summary>
    public static Uri SubscriptionRoleAssignments(string subscriptionId) => Arm($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleAssignments", "2022-04-01");

    public static Uri ManagementGroupRoleAssignments(string name) => Arm($"/providers/Microsoft.Management/managementGroups/{name}/providers/Microsoft.Authorization/roleAssignments", "2022-04-01", "$filter=atScope()");

    public static Uri SubscriptionAssignmentInstances(string subscriptionId) => Arm($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleAssignmentScheduleInstances", "2020-10-01");

    public static Uri SubscriptionEligibilityInstances(string subscriptionId) => Arm($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleEligibilityScheduleInstances", "2020-10-01");

    public static Uri SubscriptionRoleDefinitions(string subscriptionId) => Arm($"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions", "2022-04-01");

    private static Uri Graph(string path) => new(GraphTransport.GraphBase + path);

    private static Uri Arm(string path, string apiVersion, string? extra = null) =>
        new($"{ArmBase}{path}?api-version={apiVersion}{(extra is null ? string.Empty : "&" + extra)}");

    private static string Escape(string id) => Uri.EscapeDataString(id);
}
```

`audit/src/Elevate.Audit/Collectors/Wire.cs`:

```csharp
using System.Text.Json.Serialization;
using Elevate.Audit.Model;

namespace Elevate.Audit.Collectors;

/// <summary>Graph shapes shared by more than one collector, and the mapping to snapshot records.</summary>
public static class Wire
{
    public sealed record WirePrincipal(
        [property: JsonPropertyName("@odata.type")] string? OdataType,
        string Id,
        string? DisplayName,
        string? UserPrincipalName,
        string? UserType,
        bool? AccountEnabled,
        string? ServicePrincipalType);

    public sealed record ArmPage<T>(IReadOnlyList<T>? Value, string? NextLink);

    public static PrincipalType PrincipalTypeOf(string? odataType) => odataType?.ToLowerInvariant() switch
    {
        "#microsoft.graph.user" => PrincipalType.User,
        "#microsoft.graph.group" => PrincipalType.Group,
        "#microsoft.graph.serviceprincipal" => PrincipalType.ServicePrincipal,
        "#microsoft.graph.device" => PrincipalType.Device,
        "#microsoft.graph.orgcontact" => PrincipalType.Contact,
        _ => PrincipalType.Unknown,
    };

    public static AssignmentType? AssignmentTypeOf(string? value) => value?.ToLowerInvariant() switch
    {
        "assigned" => AssignmentType.Assigned,
        "activated" => AssignmentType.Activated,
        _ => null,
    };

    public static PrincipalRecord ToRecord(WirePrincipal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new PrincipalRecord(
            p.Id,
            PrincipalTypeOf(p.OdataType),
            p.DisplayName,
            p.UserPrincipalName,
            string.Equals(p.UserType, "Guest", StringComparison.OrdinalIgnoreCase),
            p.AccountEnabled,
            p.ServicePrincipalType);
    }
}
```

`audit/src/Elevate.Audit/Collectors/DirectoryRoleCollector.cs`:

```csharp
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Audit.Collectors;

public sealed record DirectoryRoleData(
    IReadOnlyList<RoleDefinitionRecord> Definitions,
    IReadOnlyList<EntraAssignmentRecord> Assignments,
    IReadOnlyList<EntraAssignmentRecord> Eligibilities,
    IReadOnlyList<PrincipalRecord> Principals);

/// <summary>Entra directory roles: definitions, every active assignment instance, every eligibility instance.</summary>
public sealed class DirectoryRoleCollector(GraphTransport graph, Identity identity, string tenantId)
{
    internal sealed record WireDefinition(string Id, string? TemplateId, string? DisplayName, bool? IsPrivileged, bool? IsBuiltIn);

    internal sealed record WireInstance(
        string Id,
        string PrincipalId,
        string RoleDefinitionId,
        string? DirectoryScopeId,
        string? AppScopeId,
        string? AssignmentType,
        string? MemberType,
        DateTimeOffset? StartDateTime,
        DateTimeOffset? EndDateTime,
        Wire.WirePrincipal? Principal);

    public async Task<DirectoryRoleData> CollectAsync(CancellationToken ct)
    {
        var scopes = ClientIds.GraphReadScopes;
        var definitions = await graph.ListAllAsync<WireDefinition>(identity, tenantId, GraphUrls.RoleDefinitions, scopes, ct).ConfigureAwait(false);
        var assignments = await graph.ListAllAsync<WireInstance>(identity, tenantId, GraphUrls.RoleAssignmentInstances, scopes, ct).ConfigureAwait(false);
        var eligibilities = await graph.ListAllAsync<WireInstance>(identity, tenantId, GraphUrls.RoleEligibilityInstances, scopes, ct).ConfigureAwait(false);

        var catalogue = RoleCatalogue.EntraBuiltInRoles().ToDictionary(r => r.TemplateId, r => r.IsPrivileged, StringComparer.OrdinalIgnoreCase);
        var principals = new Dictionary<string, PrincipalRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in assignments.Concat(eligibilities))
        {
            if (instance.Principal is { } p)
            {
                principals.TryAdd(p.Id, Wire.ToRecord(p));
            }
        }

        return new DirectoryRoleData(
            definitions.Select(d => new RoleDefinitionRecord(
                d.Id,
                d.TemplateId,
                d.DisplayName ?? d.Id,
                d.IsPrivileged ?? (d.TemplateId is { } t && catalogue.TryGetValue(t, out var flagged) && flagged),
                d.IsBuiltIn ?? false)).ToList(),
            assignments.Select(Map).ToList(),
            eligibilities.Select(Map).ToList(),
            principals.Values.ToList());
    }

    private static EntraAssignmentRecord Map(WireInstance i) => new(
        i.Id,
        i.PrincipalId,
        i.RoleDefinitionId,
        i.DirectoryScopeId ?? "/",
        i.AppScopeId,
        Wire.AssignmentTypeOf(i.AssignmentType),
        i.MemberType,
        i.StartDateTime,
        i.EndDateTime);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass. If the `$` in the raw-string test payload trips the interpolation, keep the `$$"""` form and `{{...}}` placeholders exactly as written.

- [ ] **Step 6: Commit**

```bash
git add audit
git commit -m "audit: Graph URLs, wire mapping and directory role collector"
```

---

### Task 6: Group collector (membership walk and PIM for Groups)

**Files:**
- Create: `audit/src/Elevate.Audit/Collectors/GroupCollector.cs`
- Test: `audit/tests/Elevate.Audit.Tests/GroupCollectorTests.cs`

**Interfaces:**
- Produces: `GroupCollector(GraphTransport graph, Identity identity, string tenantId)` with `Task<GroupData> CollectAsync(IEnumerable<string> seedGroupIds, CancellationToken)`; `GroupData(IReadOnlyList<GroupRecord> Groups, IReadOnlyList<PrincipalRecord> Principals, string? PimUnavailableReason)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class GroupCollectorTests
{
    private static StubHttpClient Tenant()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/groups?$filter=isAssignableToRole", """
            {"value":[{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[]}]}
            """);
        stub.On("GET", "/groups/g1/members", """
            {"value":[
              {"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Sam Chen","userPrincipalName":"sam.chen@contoso.com","userType":"Member","accountEnabled":true},
              {"@odata.type":"#microsoft.graph.group","id":"g2","displayName":"Platform Team"},
              {"@odata.type":"#microsoft.graph.device","id":"d1","displayName":"LAPTOP-1"}
            ]}
            """);
        stub.On("GET", "/groups/g2?", """{"id":"g2","displayName":"Platform Team","isAssignableToRole":false,"groupTypes":["DynamicMembership"]}""");
        stub.On("GET", "/groups/g2/members", """
            {"value":[
              {"@odata.type":"#microsoft.graph.group","id":"g1","displayName":"Tier 0 Admins"},
              {"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1","displayName":"Deploy Bot","servicePrincipalType":"Application"}
            ]}
            """);
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g1'", """
            {"value":[{"id":"gp1","principalId":"u1","groupId":"g1","accessId":"member","assignmentType":"Assigned","startDateTime":"2025-01-01T00:00:00Z","endDateTime":null}]}
            """);
        stub.On("GET", "eligibilityScheduleInstances?$filter=groupId eq 'g1'", """{"value":[]}""");
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g2'", """{"error":{"code":"Forbidden","message":"not onboarded"}}""", 403);
        return stub;
    }

    [Fact]
    public async Task Collect_WalksNestedGroups_StopsOnCycles_AndReadsPim()
    {
        var stub = Tenant();

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(["g1"], CancellationToken.None);

        data.Groups.Select(g => g.Id).Should().BeEquivalentTo(["g1", "g2"]);
        var g1 = data.Groups.Single(g => g.Id == "g1");
        g1.DirectMembers.Select(m => (m.Id, m.Type)).Should().BeEquivalentTo([("u1", PrincipalType.User), ("g2", PrincipalType.Group), ("d1", PrincipalType.Device)]);
        g1.PimStatus.Should().Be(PimStatus.Onboarded);
        g1.PimAssignments.Should().ContainSingle().Which.IsPermanent.Should().BeTrue();
        var g2 = data.Groups.Single(g => g.Id == "g2");
        g2.IsDynamic.Should().BeTrue();
        g2.IsAssignableToRole.Should().BeFalse();
        g2.PimStatus.Should().Be(PimStatus.NotOnboarded);
        data.Principals.Select(p => p.Id).Should().BeEquivalentTo(["u1", "sp1"], "devices are counted, not resolved");
        data.PimUnavailableReason.Should().BeNull();
        stub.RequestsMatching("/groups/g1/members").Should().HaveCount(1, "each group is visited once even when nested in a cycle");
    }

    [Fact]
    public async Task Collect_WhenThePimScopeIsMissing_StopsAskingAndReportsWhy()
    {
        var stub = Tenant();
        stub.On("GET", "assignmentScheduleInstances?$filter=groupId eq 'g1'",
            """{"error":{"code":"UnknownError","message":"{\"errorCode\":\"PermissionScopeNotGranted\",\"message\":\"Authorization failed due to missing permission scope PrivilegedAssignmentSchedule.Read.AzureADGroup.\"}"}}""", 403);

        var data = await new GroupCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync([], CancellationToken.None);

        data.PimUnavailableReason.Should().Contain("PrivilegedAssignmentSchedule.Read.AzureADGroup");
        data.Groups.Should().OnlyContain(g => g.PimStatus == PimStatus.Unknown);
        stub.RequestsMatching("privilegedAccess/group").Should().HaveCount(1);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

public sealed record GroupData(IReadOnlyList<GroupRecord> Groups, IReadOnlyList<PrincipalRecord> Principals, string? PimUnavailableReason);

/// <summary>
/// Every role-assignable group plus every group reached from a seed, breadth-first through direct
/// members with a visited set (so nested groups are attributed and cycles terminate), each with its
/// PIM for Groups schedule instances.
/// </summary>
public sealed class GroupCollector(GraphTransport graph, Identity identity, string tenantId)
{
    internal sealed record WireGroup(string Id, string? DisplayName, bool? IsAssignableToRole, IReadOnlyList<string>? GroupTypes);

    internal sealed record WireGroupPim(string Id, string? PrincipalId, string? GroupId, string? AccessId, string? AssignmentType, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime);

    private readonly IReadOnlyList<string> _scopes = ClientIds.GraphReadScopes;
    private string? _pimUnavailable;

    public async Task<GroupData> CollectAsync(IEnumerable<string> seedGroupIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedGroupIds);
        var known = new Dictionary<string, WireGroup>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var g in await graph.ListAllAsync<WireGroup>(identity, tenantId, GraphUrls.RoleAssignableGroups, _scopes, ct).ConfigureAwait(false))
        {
            known[g.Id] = g;
            queue.Enqueue(g.Id);
        }

        foreach (var id in seedGroupIds)
        {
            queue.Enqueue(id);
        }

        var groups = new Dictionary<string, GroupRecord>(StringComparer.OrdinalIgnoreCase);
        var principals = new Dictionary<string, PrincipalRecord>(StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var id))
        {
            if (groups.ContainsKey(id))
            {
                continue;
            }

            var meta = known.TryGetValue(id, out var k) ? k : await GetGroupAsync(id, ct).ConfigureAwait(false);
            if (meta is null)
            {
                continue; // deleted between calls
            }

            var members = await graph.ListAllAsync<Wire.WirePrincipal>(identity, tenantId, GraphUrls.GroupMembers(id), _scopes, ct).ConfigureAwait(false);
            var direct = new List<GroupMemberRecord>(members.Count);
            foreach (var m in members)
            {
                var type = Wire.PrincipalTypeOf(m.OdataType);
                direct.Add(new GroupMemberRecord(m.Id, type));
                switch (type)
                {
                    case PrincipalType.Group:
                        queue.Enqueue(m.Id);
                        break;
                    case PrincipalType.User or PrincipalType.ServicePrincipal:
                        principals.TryAdd(m.Id, Wire.ToRecord(m));
                        break;
                }
            }

            var (status, assigned, eligible) = await PimAsync(id, ct).ConfigureAwait(false);
            groups[id] = new GroupRecord(
                id,
                meta.DisplayName ?? id,
                meta.IsAssignableToRole ?? false,
                meta.GroupTypes?.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase) == true,
                status,
                direct,
                assigned,
                eligible);
        }

        return new GroupData(groups.Values.ToList(), principals.Values.ToList(), _pimUnavailable);
    }

    private async Task<WireGroup?> GetGroupAsync(string id, CancellationToken ct)
    {
        try
        {
            var response = await graph.GetAsync(identity, tenantId, GraphUrls.Group(id), _scopes, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WireGroup>(response.Body, GraphJson.Options);
        }
        catch (PimException e) when (e.Status == 404)
        {
            return null;
        }
    }

    private async Task<(PimStatus Status, IReadOnlyList<GroupPimRecord> Assigned, IReadOnlyList<GroupPimRecord> Eligible)> PimAsync(string id, CancellationToken ct)
    {
        if (_pimUnavailable is not null)
        {
            return (PimStatus.Unknown, [], []);
        }

        try
        {
            var assigned = await graph.ListAllAsync<WireGroupPim>(identity, tenantId, GraphUrls.GroupPimAssignments(id), _scopes, ct).ConfigureAwait(false);
            var eligible = await graph.ListAllAsync<WireGroupPim>(identity, tenantId, GraphUrls.GroupPimEligibilities(id), _scopes, ct).ConfigureAwait(false);
            var status = assigned.Count + eligible.Count > 0 ? PimStatus.Onboarded : PimStatus.Unknown;
            return (status, assigned.Select(Map).ToList(), eligible.Select(Map).ToList());
        }
        catch (PimException e) when (IsMissingScope(e))
        {
            _pimUnavailable = e.UserMessage;
            return (PimStatus.Unknown, [], []);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.Forbidden or PimErrorKind.ConsentRequired || e.Status == 404)
        {
            return (PimStatus.NotOnboarded, [], []);
        }
    }

    /// <summary>Core phrases a PermissionScopeNotGranted 403 as "… is not granted &lt;scope&gt; …"; that is a tenant-wide condition, not one group's.</summary>
    private static bool IsMissingScope(PimException e) =>
        e.Kind == PimErrorKind.Forbidden && e.UserMessage.Contains("is not granted", StringComparison.Ordinal);

    private static GroupPimRecord Map(WireGroupPim p) => new(
        p.Id,
        p.PrincipalId ?? string.Empty,
        p.AccessId ?? "member",
        Wire.AssignmentTypeOf(p.AssignmentType),
        p.StartDateTime,
        p.EndDateTime);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass. If the second test's "is not granted" phrase does not match, read `GraphTransport.FirstPartyForbiddenMessage` in Core and match the phrase it produces for a `Custom` sign-in method; do not change Core.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: group collector with nested walk and PIM for Groups"
```

---

### Task 7: Principal resolution

**Files:**
- Create: `audit/src/Elevate.Audit/Collectors/PrincipalCollector.cs`
- Test: `audit/tests/Elevate.Audit.Tests/PrincipalCollectorTests.cs`

**Interfaces:**
- Produces: `PrincipalCollector(GraphTransport graph, Identity identity, string tenantId)` with `Task<IReadOnlyList<PrincipalRecord>> ResolveAsync(IReadOnlyCollection<string> ids, CancellationToken)`; `PrincipalCollector.ChunkSize = 1000`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using System.Text.Json;
using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class PrincipalCollectorTests
{
    [Fact]
    public async Task Resolve_PostsInChunksOfAThousand_AndMapsTypes()
    {
        var stub = new StubHttpClient();
        stub.On("POST", "/directoryObjects/getByIds", request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            var ids = body.RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetString()!).ToList();
            var value = string.Join(",", ids.Take(2).Select((id, i) => i == 0
                ? $$"""{"@odata.type":"#microsoft.graph.user","id":"{{id}}","displayName":"Jordan Lee","userPrincipalName":"jordan.lee@contoso.com","userType":"Member"}"""
                : $$"""{"@odata.type":"#microsoft.graph.servicePrincipal","id":"{{id}}","displayName":"Backup Job","servicePrincipalType":"ManagedIdentity"}"""));
            return new Elevate.Core.Networking.HttpResponseData(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes($$"""{"value":[{{value}}]}"""));
        });
        var ids = Enumerable.Range(0, 1001).Select(i => $"p{i}").ToList();

        var resolved = await new PrincipalCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).ResolveAsync(ids, CancellationToken.None);

        stub.RequestsMatching("getByIds").Should().HaveCount(2);
        using var first = JsonDocument.Parse(stub.Requests[0].Body!);
        first.RootElement.GetProperty("ids").GetArrayLength().Should().Be(1000);
        first.RootElement.GetProperty("types").EnumerateArray().Select(e => e.GetString()).Should().Equal("user", "group", "servicePrincipal", "device");
        resolved.Should().HaveCount(3);
        resolved.Single(p => p.Id == "p0").Type.Should().Be(PrincipalType.User);
        resolved.Single(p => p.Id == "p1").Should().BeEquivalentTo(new { Type = PrincipalType.ServicePrincipal, ServicePrincipalType = "ManagedIdentity" });
    }

    [Fact]
    public async Task Resolve_WithNoIds_MakesNoRequest()
    {
        var stub = new StubHttpClient();

        var resolved = await new PrincipalCollector(TestIdentity.Graph(stub), TestIdentity.Alex, TestIdentity.TenantId).ResolveAsync([], CancellationToken.None);

        resolved.Should().BeEmpty();
        stub.Requests.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

/// <summary>Resolves bare object ids (ARM principals, limited-information members) to typed principals in one POST per 1000 ids.</summary>
public sealed class PrincipalCollector(GraphTransport graph, Identity identity, string tenantId)
{
    public const int ChunkSize = 1000;

    private static readonly string[] Types = ["user", "group", "servicePrincipal", "device"];

    public async Task<IReadOnlyList<PrincipalRecord>> ResolveAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new List<PrincipalRecord>(ids.Count);
        foreach (var chunk in ids.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(ChunkSize))
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { ids = chunk, types = Types });
            var response = await graph.PostAsync(identity, tenantId, GraphUrls.GetByIds, ClientIds.GraphReadScopes, body, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<GraphTransport.Page<Wire.WirePrincipal>>(response.Body, GraphJson.Options);
            if (page?.Value is { } value)
            {
                result.AddRange(value.Select(Wire.ToRecord));
            }
        }

        return result;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: principal resolution through getByIds"
```

---

### Task 8: Azure collector

**Files:**
- Create: `audit/src/Elevate.Audit/Collectors/AzureCollector.cs`
- Test: `audit/tests/Elevate.Audit.Tests/AzureCollectorTests.cs`

**Interfaces:**
- Produces: `AzureCollector(GraphTransport arm, Identity identity, string tenantId)` with `Task<AzureData> CollectAsync(CancellationToken)`; `AzureData(Scopes, RoleDefinitions, Assignments, Eligibilities, Notes)`; `AzureCollector.ScopeKindOf(string scope): AzureScopeKind`; `AzureCollector.ScopeDisplayName(string scope): string`. Throws `PimException(PimErrorKind.NotEligible, …)` when no subscription is visible.

- [ ] **Step 1: Write the failing test**

```csharp
using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class AzureCollectorTests
{
    private static StubHttpClient Tenant()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/providers/Microsoft.Management/managementGroups?", """{"error":{"code":"AuthorizationFailed","message":"no"}}""", 403);
        stub.On("GET", "/subscriptions?api-version", """{"value":[{"id":"/subscriptions/sub1","subscriptionId":"sub1","displayName":"Production"}]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments?", """
            {"value":[
              {"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments/ra1","name":"ra1","properties":{"scope":"/subscriptions/sub1","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","principalId":"u1","principalType":"User"}},
              {"id":"/subscriptions/sub1/resourceGroups/rg-app/providers/Microsoft.Authorization/roleAssignments/ra2","name":"ra2","properties":{"scope":"/subscriptions/sub1/resourceGroups/rg-app","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c","principalId":"g1","principalType":"Group"}}
            ]}
            """);
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignmentScheduleInstances?", """
            {"value":[{"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignmentScheduleInstances/si1","name":"si1","properties":{"scope":"/subscriptions/sub1","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","principalId":"u1","principalType":"User","assignmentType":"Assigned","startDateTime":"2025-01-01T00:00:00Z","endDateTime":null}}]}
            """);
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?", """{"value":[]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions?", """
            {"value":[
              {"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","name":"8e3af657-a8ff-443c-a75c-2fe8c4bcb635","properties":{"roleName":"Owner","type":"BuiltInRole","permissions":[{"actions":["*"]}]}},
              {"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c","name":"b24988ac-6180-42a0-ab88-20f7382dd24c","properties":{"roleName":"Contributor","type":"BuiltInRole","permissions":[{"actions":["*"],"notActions":["Microsoft.Authorization/*/Write"]}]}}
            ]}
            """);
        return stub;
    }

    [Fact]
    public async Task Collect_DegradesWithoutManagementGroups_AndMapsEverything()
    {
        var stub = Tenant();

        var data = await new AzureCollector(TestIdentity.Arm(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(CancellationToken.None);

        data.Notes.Should().ContainSingle().Which.Should().Contain("management groups");
        data.Scopes.Should().ContainSingle().Which.Should().BeEquivalentTo(new AzureScopeRecord("/subscriptions/sub1", AzureScopeKind.Subscription, "Production"));
        data.Assignments.Should().HaveCount(3);
        data.Assignments.Single(a => a.Id.EndsWith("/ra2", StringComparison.Ordinal)).Should().BeEquivalentTo(new { PrincipalId = "g1", PrincipalType = "Group", FromSchedule = false, Scope = "/subscriptions/sub1/resourceGroups/rg-app" });
        data.Assignments.Single(a => a.FromSchedule).AssignmentType.Should().Be(AssignmentType.Assigned);
        data.RoleDefinitions.Should().HaveCount(2);
        data.RoleDefinitions.Single(r => r.DisplayName == "Owner").Actions.Should().Equal("*");
    }

    [Fact]
    public async Task Collect_WithNoSubscriptions_ThrowsSoTheScannerSkipsTheSource()
    {
        var stub = Tenant();
        stub.On("GET", "/subscriptions?api-version", """{"value":[]}""");

        var act = () => new AzureCollector(TestIdentity.Arm(stub), TestIdentity.Alex, TestIdentity.TenantId).CollectAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.NotEligible);
    }

    [Theory]
    [InlineData("/providers/Microsoft.Management/managementGroups/contoso", AzureScopeKind.ManagementGroup, "contoso")]
    [InlineData("/subscriptions/sub1", AzureScopeKind.Subscription, "sub1")]
    [InlineData("/subscriptions/sub1/resourceGroups/rg-app", AzureScopeKind.ResourceGroup, "rg-app")]
    [InlineData("/subscriptions/sub1/resourceGroups/rg-app/providers/Microsoft.KeyVault/vaults/kv-prod", AzureScopeKind.Resource, "kv-prod")]
    public void ScopeKindOf_ClassifiesByPath(string scope, AzureScopeKind kind, string name)
    {
        AzureCollector.ScopeKindOf(scope).Should().Be(kind);
        AzureCollector.ScopeDisplayName(scope).Should().Be(name);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using Elevate.Audit.Model;
using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

public sealed record AzureData(
    IReadOnlyList<AzureScopeRecord> Scopes,
    IReadOnlyList<AzureRoleDefinitionRecord> RoleDefinitions,
    IReadOnlyList<AzureAssignmentRecord> Assignments,
    IReadOnlyList<AzureAssignmentRecord> Eligibilities,
    IReadOnlyList<string> Notes);

/// <summary>Azure RBAC through ARM: management groups (best effort), subscriptions, classic role assignments, PIM schedule instances, role definitions.</summary>
public sealed class AzureCollector(GraphTransport arm, Identity identity, string tenantId)
{
    internal sealed record Named(string? DisplayName);
    internal sealed record WireManagementGroup(string Id, string Name, Named? Properties);
    internal sealed record WireSubscription(string Id, string SubscriptionId, string? DisplayName);
    internal sealed record AssignmentProperties(string? Scope, string? RoleDefinitionId, string? PrincipalId, string? PrincipalType, string? AssignmentType, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime);
    internal sealed record WireAssignment(string Id, string Name, AssignmentProperties? Properties);
    internal sealed record Permission(IReadOnlyList<string>? Actions);
    internal sealed record DefinitionProperties(string? RoleName, string? Type, IReadOnlyList<Permission>? Permissions);
    internal sealed record WireDefinition(string Id, string Name, DefinitionProperties? Properties);

    private readonly IReadOnlyList<string> _scopes = Scopes.ArmAll;

    public async Task<AzureData> CollectAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var scopes = new List<AzureScopeRecord>();

        IReadOnlyList<WireManagementGroup> managementGroups = [];
        try
        {
            managementGroups = await ListAllAsync<WireManagementGroup>(GraphUrls.ManagementGroups, ct).ConfigureAwait(false);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.PolicyViolation or PimErrorKind.Forbidden)
        {
            notes.Add("Azure management groups are not readable by this account; scanned subscriptions only.");
        }

        var subscriptions = await ListAllAsync<WireSubscription>(GraphUrls.Subscriptions, ct).ConfigureAwait(false);
        if (subscriptions.Count == 0)
        {
            throw new PimException(PimErrorKind.NotEligible, "No Azure subscriptions are visible to this account");
        }

        scopes.AddRange(managementGroups.Select(m => new AzureScopeRecord(m.Id, AzureScopeKind.ManagementGroup, m.Properties?.DisplayName ?? m.Name)));
        scopes.AddRange(subscriptions.Select(s => new AzureScopeRecord(s.Id, AzureScopeKind.Subscription, s.DisplayName ?? s.SubscriptionId)));

        var assignments = new Dictionary<string, AzureAssignmentRecord>(StringComparer.OrdinalIgnoreCase);
        var eligibilities = new Dictionary<string, AzureAssignmentRecord>(StringComparer.OrdinalIgnoreCase);
        var definitions = new Dictionary<string, AzureRoleDefinitionRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var mg in managementGroups)
        {
            foreach (var a in await ListAllAsync<WireAssignment>(GraphUrls.ManagementGroupRoleAssignments(mg.Name), ct).ConfigureAwait(false))
            {
                assignments.TryAdd(a.Id, Map(a, fromSchedule: false));
            }
        }

        foreach (var sub in subscriptions)
        {
            foreach (var a in await ListAllAsync<WireAssignment>(GraphUrls.SubscriptionRoleAssignments(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                assignments.TryAdd(a.Id, Map(a, fromSchedule: false));
            }

            foreach (var a in await ListAllAsync<WireAssignment>(GraphUrls.SubscriptionAssignmentInstances(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                assignments.TryAdd(a.Id, Map(a, fromSchedule: true));
            }

            foreach (var e in await ListAllAsync<WireAssignment>(GraphUrls.SubscriptionEligibilityInstances(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                eligibilities.TryAdd(e.Id, Map(e, fromSchedule: true));
            }

            foreach (var d in await ListAllAsync<WireDefinition>(GraphUrls.SubscriptionRoleDefinitions(sub.SubscriptionId), ct).ConfigureAwait(false))
            {
                definitions.TryAdd(d.Name, new AzureRoleDefinitionRecord(
                    d.Id,
                    d.Name,
                    d.Properties?.RoleName ?? d.Name,
                    d.Properties?.Type ?? "CustomRole",
                    d.Properties?.Permissions?.SelectMany(p => p.Actions ?? []).ToList() ?? []));
            }
        }

        return new AzureData(scopes, definitions.Values.ToList(), assignments.Values.ToList(), eligibilities.Values.ToList(), notes);
    }

    public static AzureScopeKind ScopeKindOf(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var parts = scope.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("providers", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("Microsoft.Management", StringComparison.OrdinalIgnoreCase))
        {
            return AzureScopeKind.ManagementGroup;
        }

        return parts.Length switch
        {
            <= 2 => AzureScopeKind.Subscription,
            4 when parts[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) => AzureScopeKind.ResourceGroup,
            _ => AzureScopeKind.Resource,
        };
    }

    public static string ScopeDisplayName(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "/";
    }

    private static AzureAssignmentRecord Map(WireAssignment a, bool fromSchedule) => new(
        a.Id,
        a.Properties?.Scope ?? "/",
        a.Properties?.RoleDefinitionId ?? string.Empty,
        a.Properties?.PrincipalId ?? string.Empty,
        a.Properties?.PrincipalType,
        Wire.AssignmentTypeOf(a.Properties?.AssignmentType),
        a.Properties?.StartDateTime,
        a.Properties?.EndDateTime,
        fromSchedule);

    /// <summary>ARM pages with <c>nextLink</c>, not <c>@odata.nextLink</c>; Core's helper for that is internal, so this is ours.</summary>
    private async Task<IReadOnlyList<T>> ListAllAsync<T>(Uri url, CancellationToken ct)
    {
        Uri? next = url;
        var all = new List<T>();
        while (next is { } current)
        {
            var response = await arm.GetAsync(identity, tenantId, current, _scopes, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<Wire.ArmPage<T>>(response.Body, GraphJson.Options);
            if (page?.Value is { } items)
            {
                all.AddRange(items);
            }

            next = page?.NextLink is { } link && Uri.TryCreate(link, UriKind.Absolute, out var parsed) ? parsed : null;
        }

        return all;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass. `GraphTransport.MapArmError` turns the management-group 403 into `PolicyViolation`, which the catch above handles.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: Azure RBAC collector"
```

---

### Task 9: Scanner orchestration

**Files:**
- Create: `audit/src/Elevate.Audit/Collectors/Scanner.cs`
- Test: `audit/tests/Elevate.Audit.Tests/ScannerTests.cs`

**Interfaces:**
- Consumes: the four collectors.
- Produces: `Scanner(GraphTransport graph, GraphTransport? arm, Identity identity, string tenantId, AuditOptions options, string toolVersion, Action<string> note, TimeProvider? clock = null)` with `Task<Snapshot> ScanAsync(CancellationToken)`. Source names used in `SkippedSource.Source`: `"azure"`, `"pim-for-groups"`, `"principals"`.

- [ ] **Step 1: Write the failing test**

```csharp
using Elevate.Audit.Collectors;
using Elevate.Audit.Model;
using Elevate.Audit.Tests.Support;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ScannerTests
{
    private static StubHttpClient Tenant()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/organization", """{"value":[{"id":"11111111-1111-1111-1111-111111111111","displayName":"Contoso"}]}""");
        stub.On("GET", "/roleManagement/directory/roleDefinitions", """{"value":[{"id":"rd-ga","templateId":"62e90394-69f5-4237-9190-012177145e10","displayName":"Global Administrator","isPrivileged":true,"isBuiltIn":true}]}""");
        stub.On("GET", "roleAssignmentScheduleInstances?$expand", """
            {"value":[{"id":"a1","principalId":"g1","roleDefinitionId":"rd-ga","directoryScopeId":"/","assignmentType":"Assigned","memberType":"Direct","principal":{"@odata.type":"#microsoft.graph.group","id":"g1","displayName":"Tier 0 Admins"}}]}
            """);
        stub.On("GET", "roleEligibilityScheduleInstances", """{"value":[]}""");
        stub.On("GET", "/groups?$filter=isAssignableToRole", """{"value":[{"id":"g1","displayName":"Tier 0 Admins","isAssignableToRole":true,"groupTypes":[]}]}""");
        stub.On("GET", "/groups/g1/members", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u1","displayName":"Sam Chen","userPrincipalName":"sam.chen@contoso.com","userType":"Member"}]}""");
        stub.On("GET", "privilegedAccess/group", """{"value":[]}""");
        stub.On("POST", "/directoryObjects/getByIds", """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u9","displayName":"Jordan Lee","userPrincipalName":"jordan.lee@contoso.com","userType":"Member"}]}""");
        stub.On("GET", "/providers/Microsoft.Management/managementGroups?", """{"value":[]}""");
        stub.On("GET", "/subscriptions?api-version", """{"value":[{"id":"/subscriptions/sub1","subscriptionId":"sub1","displayName":"Production"}]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments?", """
            {"value":[{"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleAssignments/ra1","name":"ra1","properties":{"scope":"/subscriptions/sub1","roleDefinitionId":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","principalId":"u9","principalType":"User"}}]}
            """);
        stub.On("GET", "roleAssignmentScheduleInstances?api-version", """{"value":[]}""");
        stub.On("GET", "roleEligibilityScheduleInstances?api-version", """{"value":[]}""");
        stub.On("GET", "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions?", """{"value":[{"id":"/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635","name":"8e3af657-a8ff-443c-a75c-2fe8c4bcb635","properties":{"roleName":"Owner","type":"BuiltInRole","permissions":[{"actions":["*"]}]}}]}""");
        return stub;
    }

    private static Scanner Build(StubHttpClient stub, AuditOptions? options = null, bool withArm = true) => new(
        TestIdentity.Graph(stub),
        withArm ? TestIdentity.Arm(stub) : null,
        TestIdentity.Alex,
        TestIdentity.TenantId,
        options ?? new AuditOptions(),
        "1.2.3",
        _ => { },
        new FakeTimeProvider(DateTimeOffset.Parse("2026-09-13T12:00:00Z")));

    [Fact]
    public async Task Scan_AssemblesTheSnapshot_AndResolvesUnknownPrincipals()
    {
        var stub = Tenant();

        var snapshot = await Build(stub).ScanAsync(CancellationToken.None);

        snapshot.Kind.Should().Be(Snapshot.KindMarker);
        snapshot.ToolVersion.Should().Be("1.2.3");
        snapshot.Tenant.Should().Be(new TenantInfo(TestIdentity.TenantId, "Contoso"));
        snapshot.Account.Should().Be("alex.rivera@contoso.com");
        snapshot.ScannedAt.Should().Be(DateTimeOffset.Parse("2026-09-13T12:00:00Z"));
        snapshot.EntraAssignments.Should().ContainSingle();
        snapshot.Groups.Should().ContainSingle().Which.DirectMembers.Should().ContainSingle();
        snapshot.Principals.Select(p => p.Id).Should().BeEquivalentTo(["g1", "u1", "u9"], "u9 came only from ARM and was resolved through getByIds");
        snapshot.AzureAssignments.Should().ContainSingle();
        snapshot.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_WhenAzureFails_SkipsTheSourceAndContinues()
    {
        var stub = Tenant();
        stub.On("GET", "/subscriptions?api-version", """{"error":{"code":"Boom","message":"ARM is down"}}""", 500);

        var snapshot = await Build(stub).ScanAsync(CancellationToken.None);

        snapshot.Skipped.Should().ContainSingle(s => s.Source == "azure").Which.Reason.Should().Contain("ARM is down");
        snapshot.AzureAssignments.Should().BeEmpty();
        snapshot.EntraAssignments.Should().ContainSingle();
    }

    [Fact]
    public async Task Scan_WithSkipAzure_NeverCallsArm()
    {
        var stub = Tenant();

        await Build(stub, new AuditOptions(SkipAzure: true)).ScanAsync(CancellationToken.None);

        stub.RequestsMatching("management.azure.com").Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_WhenDirectoryRolesAreForbidden_Throws()
    {
        var stub = Tenant();
        stub.On("GET", "roleAssignmentScheduleInstances?$expand", """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges"}}""", 403);

        var act = () => Build(stub).ScanAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<PimException>()).Which.Kind.Should().Be(PimErrorKind.Forbidden);
    }
}
```

Add `audit/tests/Elevate.Audit.Tests/Support/FakeTimeProvider.cs`:

```csharp
namespace Elevate.Audit.Tests.Support;

public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Elevate.Core.Support;

namespace Elevate.Audit.Collectors;

/// <summary>
/// Runs the collectors and assembles the <see cref="Snapshot"/>. Directory roles are required; Azure,
/// PIM for Groups and principal resolution degrade to a <see cref="SkippedSource"/> entry.
/// </summary>
public sealed class Scanner(
    GraphTransport graph,
    GraphTransport? arm,
    Identity identity,
    string tenantId,
    AuditOptions options,
    string toolVersion,
    Action<string> note,
    TimeProvider? clock = null)
{
    private sealed record WireOrganization(string Id, string? DisplayName);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<Snapshot> ScanAsync(CancellationToken ct)
    {
        var skipped = new List<SkippedSource>();

        note("Reading directory roles…");
        var directoryTask = new DirectoryRoleCollector(graph, identity, tenantId).CollectAsync(ct);
        var azureTask = arm is not null && !options.SkipAzure ? CollectAzureAsync(arm, skipped, ct) : Task.FromResult<AzureData?>(null);
        var tenantTask = ReadTenantAsync(ct);

        var directory = await directoryTask.ConfigureAwait(false);
        var azure = await azureTask.ConfigureAwait(false);
        var tenant = await tenantTask.ConfigureAwait(false);
        if (azure is null && options.SkipAzure)
        {
            skipped.Add(new SkippedSource("azure", "skipped with --skip-azure"));
        }

        var principals = directory.Principals.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var seeds = directory.Assignments.Where(a => a.IsPermanent)
            .Select(a => a.PrincipalId)
            .Where(id => principals.TryGetValue(id, out var p) && p.Type == PrincipalType.Group)
            .Concat(azure?.Assignments.Where(a => string.Equals(a.PrincipalType, "Group", StringComparison.OrdinalIgnoreCase)).Select(a => a.PrincipalId) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        note("Expanding groups…");
        var groups = await new GroupCollector(graph, identity, tenantId).CollectAsync(seeds, ct).ConfigureAwait(false);
        foreach (var p in groups.Principals)
        {
            principals.TryAdd(p.Id, p);
        }

        if (groups.PimUnavailableReason is { } reason)
        {
            skipped.Add(new SkippedSource("pim-for-groups", reason));
        }

        var referenced = directory.Assignments.Select(a => a.PrincipalId)
            .Concat(directory.Eligibilities.Select(e => e.PrincipalId))
            .Concat(groups.Groups.SelectMany(g => g.PimAssignments.Concat(g.PimEligibilities)).Select(p => p.PrincipalId))
            .Concat(azure?.Assignments.Select(a => a.PrincipalId) ?? [])
            .Concat(azure?.Eligibilities.Select(a => a.PrincipalId) ?? [])
            .Where(id => !string.IsNullOrEmpty(id) && !principals.ContainsKey(id) && !groups.Groups.Any(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (referenced.Count > 0)
        {
            note($"Resolving {referenced.Count} principals…");
            try
            {
                foreach (var p in await new PrincipalCollector(graph, identity, tenantId).ResolveAsync(referenced, ct).ConfigureAwait(false))
                {
                    principals.TryAdd(p.Id, p);
                }
            }
            catch (PimException e)
            {
                skipped.Add(new SkippedSource("principals", $"Some principals could not be resolved to names: {e.UserMessage}"));
            }
        }

        skipped.AddRange((azure?.Notes ?? []).Select(n => new SkippedSource("azure-management-groups", n)));

        return new Snapshot(
            Snapshot.KindMarker,
            toolVersion,
            tenant,
            identity.Upn,
            _clock.GetUtcNow(),
            directory.Definitions,
            directory.Assignments,
            directory.Eligibilities,
            groups.Groups,
            principals.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(),
            azure?.Scopes ?? [],
            azure?.RoleDefinitions ?? [],
            azure?.Assignments ?? [],
            azure?.Eligibilities ?? [],
            skipped);
    }

    private async Task<TenantInfo> ReadTenantAsync(CancellationToken ct)
    {
        try
        {
            var response = await graph.GetAsync(identity, tenantId, GraphUrls.Organization, ClientIds.GraphReadScopes, ct).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<GraphTransport.Page<WireOrganization>>(response.Body, GraphJson.Options);
            var org = page?.Value.FirstOrDefault();
            return new TenantInfo(org?.Id ?? tenantId, org?.DisplayName);
        }
        catch (PimException)
        {
            return new TenantInfo(tenantId, null);
        }
    }

    private async Task<AzureData?> CollectAzureAsync(GraphTransport transport, List<SkippedSource> skipped, CancellationToken ct)
    {
        try
        {
            note("Reading Azure role assignments…");
            return await new AzureCollector(transport, identity, tenantId).CollectAsync(ct).ConfigureAwait(false);
        }
        catch (PimException e)
        {
            lock (skipped)
            {
                skipped.Add(new SkippedSource("azure", e.UserMessage));
            }

            return null;
        }
    }
}
```

Note: `tenantId` passed to the scanner is the tenant the token provider signs into; when it is `organizations`, `ReadTenantAsync` replaces it with the real id from `/organization`, and `identity.HomeTenantId` is the fallback if that call fails. Make that explicit: in `ReadTenantAsync`, use `org?.Id ?? (Guid.TryParse(tenantId, out _) ? tenantId : identity.HomeTenantId)`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass. The forbidden test relies on `GraphTransport` turning a 403 into `Forbidden` for a `Custom` sign-in method; `TestIdentity.Alex` uses `SignInMethod.Custom`, so it does.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: scanner assembling the snapshot with degraded sources"
```

---

### Task 10: Rule infrastructure — fixture builder, context, group expansion, privilege, runner

**Files:**
- Create: `audit/src/Elevate.Audit/Rules/IRule.cs`, `audit/src/Elevate.Audit/Rules/GroupExpansion.cs`, `audit/src/Elevate.Audit/Rules/Privilege.cs`, `audit/src/Elevate.Audit/Rules/PortalLinks.cs`, `audit/src/Elevate.Audit/Rules/RuleRunner.cs`, `audit/src/Elevate.Audit/Resources/AzurePrivilegedRoles.json`
- Modify: `audit/src/Elevate.Audit/Elevate.Audit.csproj` (embedded resource)
- Create: `audit/tests/Elevate.Audit.Tests/Support/SnapshotBuilder.cs`, `audit/tests/Elevate.Audit.Tests/RuleInfrastructureTests.cs`

**Interfaces:**
- Produces: `IRule { string Code { get; } IEnumerable<Finding> Evaluate(RuleContext context); }`; `RuleContext` (members listed in code); `GroupExpansion.Expand(string groupId): IReadOnlyList<GroupExpansion.Member>`; `Privilege.AzureSeverityFor(AzureRoleDefinitionRecord): Severity?`; `PortalLinks.*`; `RuleRunner.All`, `RuleRunner.Run(Snapshot, AuditOptions, IEnumerable<IRule>? rules = null): IReadOnlyList<Finding>`, `RuleRunner.Visible(IReadOnlyList<Finding>, AuditOptions)`, `RuleRunner.HasHigh(IEnumerable<Finding>)`. Test `SnapshotBuilder` (fluent, see code).

- [ ] **Step 1: The fixture builder**

`audit/tests/Elevate.Audit.Tests/Support/SnapshotBuilder.cs`:

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Tests.Support;

/// <summary>Fluent fixture for rule and renderer tests. Ids are short strings; the rules never parse them.</summary>
public sealed class SnapshotBuilder
{
    public const string GlobalAdminTemplate = "62e90394-69f5-4237-9190-012177145e10";
    public const string AzureRolePrefix = "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/";

    private readonly List<RoleDefinitionRecord> _roles = [];
    private readonly List<EntraAssignmentRecord> _assignments = [];
    private readonly List<EntraAssignmentRecord> _eligibilities = [];
    private readonly Dictionary<string, GroupRecord> _groups = new(StringComparer.Ordinal);
    private readonly List<PrincipalRecord> _principals = [];
    private readonly List<AzureScopeRecord> _azureScopes = [];
    private readonly List<AzureRoleDefinitionRecord> _azureRoles = [];
    private readonly List<AzureAssignmentRecord> _azureAssignments = [];
    private readonly List<AzureAssignmentRecord> _azureEligibilities = [];
    private readonly List<SkippedSource> _skipped = [];
    private TenantInfo _tenant = new("11111111-1111-1111-1111-111111111111", "Contoso");

    public static SnapshotBuilder Contoso() => new SnapshotBuilder()
        .EntraRole("rd-ga", GlobalAdminTemplate, "Global Administrator", privileged: true)
        .EntraRole("rd-pra", "e8611ab8-c189-46e8-94e1-60213ab1f814", "Privileged Role Administrator", privileged: true)
        .EntraRole("rd-reader", "f2ef992c-3afb-46b9-b7cf-a126ee74c451", "Global Reader", privileged: false)
        .AzureRole("8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "Owner", actions: "*")
        .AzureRole("b24988ac-6180-42a0-ab88-20f7382dd24c", "Contributor", actions: "*")
        .AzureRole("acdd72a7-3385-48ef-bd42-f606fba81ae7", "Reader", actions: "*/read")
        .AzureScope("/subscriptions/sub1", AzureScopeKind.Subscription, "Production");

    public SnapshotBuilder Tenant(string id, string? name) { _tenant = new TenantInfo(id, name); return this; }

    public SnapshotBuilder User(string id, string name, string upn, bool guest = false, bool? enabled = true)
    {
        _principals.Add(new PrincipalRecord(id, PrincipalType.User, name, upn, guest, enabled, null));
        return this;
    }

    public SnapshotBuilder ServicePrincipal(string id, string name, string type = "Application")
    {
        _principals.Add(new PrincipalRecord(id, PrincipalType.ServicePrincipal, name, null, false, true, type));
        return this;
    }

    public SnapshotBuilder Group(string id, string name, bool assignable = true, bool dynamic = false, PimStatus pim = PimStatus.Unknown, params (string Id, PrincipalType Type)[] members)
    {
        _groups[id] = new GroupRecord(id, name, assignable, dynamic, pim, members.Select(m => new GroupMemberRecord(m.Id, m.Type)).ToList(), [], []);
        return this;
    }

    public SnapshotBuilder EntraRole(string id, string? template, string name, bool privileged, bool builtIn = true)
    {
        _roles.Add(new RoleDefinitionRecord(id, template, name, privileged, builtIn));
        return this;
    }

    public SnapshotBuilder Assigned(string id, string principalId, string roleId, string scope = "/", DateTimeOffset? end = null, AssignmentType type = AssignmentType.Assigned, string memberType = "Direct")
    {
        _assignments.Add(new EntraAssignmentRecord(id, principalId, roleId, scope, null, type, memberType, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end));
        return this;
    }

    public SnapshotBuilder Eligible(string id, string principalId, string roleId, DateTimeOffset? end = null)
    {
        _eligibilities.Add(new EntraAssignmentRecord(id, principalId, roleId, "/", null, null, "Direct", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end));
        return this;
    }

    public SnapshotBuilder GroupPim(string groupId, string id, string principalId, string accessId = "member", AssignmentType? type = AssignmentType.Assigned, DateTimeOffset? end = null, bool eligible = false)
    {
        var group = _groups[groupId];
        var record = new GroupPimRecord(id, principalId, accessId, eligible ? null : type, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end);
        _groups[groupId] = eligible
            ? group with { PimStatus = PimStatus.Onboarded, PimEligibilities = [.. group.PimEligibilities, record] }
            : group with { PimStatus = PimStatus.Onboarded, PimAssignments = [.. group.PimAssignments, record] };
        return this;
    }

    public SnapshotBuilder AzureScope(string id, AzureScopeKind kind, string name) { _azureScopes.Add(new AzureScopeRecord(id, kind, name)); return this; }

    public SnapshotBuilder AzureRole(string guid, string name, string type = "BuiltInRole", params string[] actions)
    {
        _azureRoles.Add(new AzureRoleDefinitionRecord(AzureRolePrefix + guid, guid, name, type, actions));
        return this;
    }

    public SnapshotBuilder AzureAssigned(string id, string scope, string roleGuid, string principalId, string principalType, AssignmentType? type = null, DateTimeOffset? end = null, bool fromSchedule = false)
    {
        _azureAssignments.Add(new AzureAssignmentRecord(id, scope, AzureRolePrefix + roleGuid, principalId, principalType, type, fromSchedule ? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) : null, end, fromSchedule));
        return this;
    }

    public SnapshotBuilder AzureEligible(string id, string scope, string roleGuid, string principalId, string principalType, DateTimeOffset? end = null)
    {
        _azureEligibilities.Add(new AzureAssignmentRecord(id, scope, AzureRolePrefix + roleGuid, principalId, principalType, null, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), end, true));
        return this;
    }

    public SnapshotBuilder Skipped(string source, string reason) { _skipped.Add(new SkippedSource(source, reason)); return this; }

    public Snapshot Build() => new(
        Snapshot.KindMarker, "0.0.0-test", _tenant, "alex.rivera@contoso.com", new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero),
        _roles, _assignments, _eligibilities, _groups.Values.ToList(), _principals,
        _azureScopes, _azureRoles, _azureAssignments, _azureEligibilities, _skipped);
}
```

- [ ] **Step 2: Write the failing tests**

`audit/tests/Elevate.Audit.Tests/RuleInfrastructureTests.cs`:

```csharp
using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class RuleInfrastructureTests
{
    [Fact]
    public void Expand_ReturnsUsersWithTheirPath_TerminatesOnCycles_AndSkipsDevices()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .ServicePrincipal("sp1", "Deploy Bot")
            .Group("g1", "Tier 0 Admins", members: [("u1", PrincipalType.User), ("g2", PrincipalType.Group), ("d1", PrincipalType.Device)])
            .Group("g2", "Platform Team", assignable: false, members: [("u2", PrincipalType.User), ("sp1", PrincipalType.ServicePrincipal), ("g1", PrincipalType.Group)])
            .Build();
        var context = new RuleContext(snapshot, new AuditOptions());

        var members = context.Expansion.Expand("g1");

        members.Select(m => m.PrincipalId).Should().Equal("u1", "u2", "sp1");
        members.Single(m => m.PrincipalId == "u1").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins");
        members.Single(m => m.PrincipalId == "u2").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins", "Platform Team");
        context.Expansion.NestedGroups("g1").Select(g => g.Id).Should().BeEquivalentTo(["g1", "g2"]);
    }

    [Fact]
    public void Expand_UnknownGroup_IsEmpty()
    {
        var context = new RuleContext(SnapshotBuilder.Contoso().Build(), new AuditOptions());

        context.Expansion.Expand("missing").Should().BeEmpty();
    }

    [Fact]
    public void Privilege_UsesTheEntraFlag_AndTheAzureList()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .AzureRole("11111111-0000-0000-0000-000000000001", "Custom Deployer", "CustomRole", "Microsoft.Compute/*", "Microsoft.Authorization/roleAssignments/write")
            .AzureRole("11111111-0000-0000-0000-000000000002", "Custom Viewer", "CustomRole", "*/read")
            .Build();
        var context = new RuleContext(snapshot, new AuditOptions());

        context.IsPrivilegedEntra("rd-ga").Should().BeTrue();
        context.IsPrivilegedEntra("rd-reader").Should().BeFalse();
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "8e3af657-a8ff-443c-a75c-2fe8c4bcb635").Should().Be(Severity.High);
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "b24988ac-6180-42a0-ab88-20f7382dd24c").Should().Be(Severity.Medium);
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "acdd72a7-3385-48ef-bd42-f606fba81ae7").Should().BeNull();
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "11111111-0000-0000-0000-000000000001").Should().Be(Severity.Medium, "a custom role that can write role assignments is privileged");
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "11111111-0000-0000-0000-000000000002").Should().BeNull();
    }

    [Fact]
    public void AllRoles_MakesEverythingPrivileged()
    {
        var context = new RuleContext(SnapshotBuilder.Contoso().Build(), new AuditOptions(AllRoles: true));

        context.IsPrivilegedEntra("rd-reader").Should().BeTrue();
        context.AzureSeverity(SnapshotBuilder.AzureRolePrefix + "acdd72a7-3385-48ef-bd42-f606fba81ae7").Should().Be(Severity.Medium);
    }

    [Fact]
    public void Principal_FallsBackToGroupsAndThenToUnknown()
    {
        var context = new RuleContext(SnapshotBuilder.Contoso().Group("g1", "Tier 0 Admins").Build(), new AuditOptions());

        context.Principal("g1").Should().BeEquivalentTo(new { DisplayName = "Tier 0 Admins", Type = PrincipalType.Group });
        context.Principal("nobody").Should().BeEquivalentTo(new { Id = "nobody", DisplayName = "<unknown principal nobody>", Type = PrincipalType.Unknown });
    }

    [Fact]
    public void Runner_SortsBySeverity_HonoursIgnore_AndFiltersVisibility()
    {
        var findings = RuleRunner.Run(SnapshotBuilder.Contoso().Build(), new AuditOptions(Ignored: ["ga-count"]), [new FakeRule("X-LOW", Severity.Low), new FakeRule("GA-COUNT", Severity.High), new FakeRule("Y-HIGH", Severity.High)]);

        findings.Select(f => f.Id).Should().Equal("Y-HIGH", "X-LOW");
        RuleRunner.HasHigh(findings).Should().BeTrue();
        RuleRunner.Visible(findings, new AuditOptions(MinSeverity: Severity.Medium)).Select(f => f.Id).Should().Equal("Y-HIGH");
        RuleRunner.All.Select(r => r.Code).Should().HaveCount(10).And.OnlyHaveUniqueItems();
    }

    private sealed class FakeRule(string code, Severity severity) : IRule
    {
        public string Code => code;

        public IEnumerable<Finding> Evaluate(RuleContext context)
        {
            yield return new Finding(code, severity,
                new FindingPrincipal("p", "P", null, PrincipalType.User, false, true),
                new FindingRole("r", "R", null, true, RoleSystem.Entra),
                new FindingScope("/", "Directory", ScopeKind.Directory), [], "fix", "https://example.invalid",
                new FindingEvidence("a", null, null, null, null));
        }
    }
}
```

`RuleRunner.All` having ten rules will fail until Tasks 11–13 land; in this task register the rules as they exist and let that one assertion stay red until Task 13 (note it in the commit message). Everything else must pass now.

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 4: Implement**

Add to `Elevate.Audit.csproj`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Resources\AzurePrivilegedRoles.json" LogicalName="Elevate.Audit.Resources.AzurePrivilegedRoles.json" />
  </ItemGroup>
```

`audit/src/Elevate.Audit/Resources/AzurePrivilegedRoles.json`:

```json
[
  { "name": "Owner", "severity": "high" },
  { "name": "User Access Administrator", "severity": "high" },
  { "name": "Role Based Access Control Administrator", "severity": "high" },
  { "name": "Contributor", "severity": "medium" },
  { "name": "Key Vault Administrator", "severity": "medium" },
  { "name": "Key Vault Data Access Administrator", "severity": "medium" },
  { "name": "Storage Account Contributor", "severity": "medium" },
  { "name": "Virtual Machine Administrator Login", "severity": "medium" },
  { "name": "Azure Kubernetes Service RBAC Cluster Admin", "severity": "medium" },
  { "name": "Security Admin", "severity": "medium" }
]
```

`audit/src/Elevate.Audit/Rules/Privilege.cs`:

```csharp
using System.Text.Json;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

/// <summary>Which Azure roles count as privileged: the bundled list by name, plus custom roles that can do anything or hand out roles.</summary>
public static class Privilege
{
    internal const string ResourceName = "Elevate.Audit.Resources.AzurePrivilegedRoles.json";

    internal sealed record AzureRoleEntry(string Name, string Severity);

    private static readonly Lazy<IReadOnlyDictionary<string, Severity>> AzureList = new(Load);

    public static Severity? AzureSeverityFor(AzureRoleDefinitionRecord role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (AzureList.Value.TryGetValue(role.DisplayName, out var severity))
        {
            return severity;
        }

        if (!string.Equals(role.Type, "BuiltInRole", StringComparison.OrdinalIgnoreCase)
            && role.Actions.Any(a => a == "*" || a.StartsWith("Microsoft.Authorization/", StringComparison.OrdinalIgnoreCase) && a.EndsWith("/write", StringComparison.OrdinalIgnoreCase)))
        {
            return Severity.Medium;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, Severity> Load()
    {
        using var stream = typeof(Privilege).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("AzurePrivilegedRoles.json missing from the bundle");
        var entries = JsonSerializer.Deserialize<List<AzureRoleEntry>>(stream, AuditJson.Options) ?? [];
        return entries.ToDictionary(e => e.Name, e => Severities.Parse(e.Severity), StringComparer.OrdinalIgnoreCase);
    }
}
```

`audit/src/Elevate.Audit/Rules/GroupExpansion.cs`:

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

/// <summary>Breadth-first walk of direct members with a visited set: every user or service principal reachable from a group, with the path that reaches it first.</summary>
public sealed class GroupExpansion(IReadOnlyDictionary<string, GroupRecord> groups)
{
    public sealed record Member(string PrincipalId, PrincipalType Type, IReadOnlyList<GroupRef> Via);

    private readonly Dictionary<string, IReadOnlyList<Member>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Member> Expand(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        if (_cache.TryGetValue(groupId, out var cached))
        {
            return cached;
        }

        var result = new List<Member>();
        var seenPrincipals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, IReadOnlyList<GroupRef> Via)>();
        queue.Enqueue((groupId, []));
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current.Id) || !groups.TryGetValue(current.Id, out var group))
            {
                continue;
            }

            var via = new List<GroupRef>(current.Via) { new(group.Id, group.DisplayName) };
            foreach (var member in group.DirectMembers)
            {
                switch (member.Type)
                {
                    case PrincipalType.Group:
                        queue.Enqueue((member.Id, via));
                        break;
                    case PrincipalType.User or PrincipalType.ServicePrincipal:
                        if (seenPrincipals.Add(member.Id))
                        {
                            result.Add(new Member(member.Id, member.Type, via));
                        }

                        break;
                }
            }
        }

        _cache[groupId] = result;
        return result;
    }

    /// <summary>The group itself and every group nested under it.</summary>
    public IReadOnlyList<GroupRecord> NestedGroups(string groupId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<GroupRecord>();
        var queue = new Queue<string>([groupId]);
        while (queue.TryDequeue(out var id))
        {
            if (!visited.Add(id) || !groups.TryGetValue(id, out var group))
            {
                continue;
            }

            result.Add(group);
            foreach (var m in group.DirectMembers.Where(m => m.Type == PrincipalType.Group))
            {
                queue.Enqueue(m.Id);
            }
        }

        return result;
    }
}
```

`audit/src/Elevate.Audit/Rules/PortalLinks.cs`:

```csharp
namespace Elevate.Audit.Rules;

/// <summary>Deep links into the Entra admin center and the Azure portal, for remedies.</summary>
public static class PortalLinks
{
    public const string EntraRoles = "https://entra.microsoft.com/#view/Microsoft_Azure_PIMCommon/ResourceMenuBlade/~/roleassignments/resourceId//resourceType/tenant/provider/aadroles";

    public static string Group(string groupId) => $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupDetailsMenuBlade/~/Overview/groupId/{Uri.EscapeDataString(groupId)}";

    public static string GroupPim(string groupId) => $"https://entra.microsoft.com/#view/Microsoft_Azure_PIMCommon/ResourceMenuBlade/~/members/resourceId/{Uri.EscapeDataString(groupId)}/resourceType/Security/provider/aadgroup";

    public static string User(string userId) => $"https://entra.microsoft.com/#view/Microsoft_AAD_UsersAndTenants/UserProfileMenuBlade/~/overview/userId/{Uri.EscapeDataString(userId)}";

    public static string AzureScope(string scope) => $"https://portal.azure.com/#@/resource{scope}/users";
}
```

`audit/src/Elevate.Audit/Rules/IRule.cs`:

```csharp
using Elevate.Audit.Collectors;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public interface IRule
{
    string Code { get; }

    IEnumerable<Finding> Evaluate(RuleContext context);
}

/// <summary>A permanent privileged assignment and one principal that holds it, directly (<see cref="Via"/> empty) or through groups.</summary>
public sealed record EntraHolder(EntraAssignmentRecord Assignment, PrincipalRecord Principal, IReadOnlyList<GroupRef> Via);

public sealed record AzureHolder(AzureAssignmentRecord Assignment, PrincipalRecord Principal, IReadOnlyList<GroupRef> Via, Severity Severity);

/// <summary>The snapshot with indexes and the shared lookups every rule needs. Built once per run.</summary>
public sealed class RuleContext
{
    public RuleContext(Snapshot snapshot, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        Options = options ?? new AuditOptions();
        Principals = snapshot.Principals.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        EntraRoles = snapshot.EntraRoleDefinitions.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Groups = snapshot.Groups.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        AzureRoles = snapshot.AzureRoleDefinitions.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Expansion = new GroupExpansion(Groups);
    }

    public Snapshot Snapshot { get; }
    public AuditOptions Options { get; }
    public IReadOnlyDictionary<string, PrincipalRecord> Principals { get; }
    public IReadOnlyDictionary<string, RoleDefinitionRecord> EntraRoles { get; }
    public IReadOnlyDictionary<string, GroupRecord> Groups { get; }
    /// <summary>Azure role definitions by their GUID name (the last segment of a roleDefinitionId).</summary>
    public IReadOnlyDictionary<string, AzureRoleDefinitionRecord> AzureRoles { get; }
    public GroupExpansion Expansion { get; }

    public bool IsPrivilegedEntra(string roleDefinitionId) =>
        Options.AllRoles || (EntraRoles.TryGetValue(roleDefinitionId, out var role) && role.IsPrivileged);

    /// <summary>Null when the role is not privileged (and <c>--all-roles</c> is off); Medium for an unlisted role under <c>--all-roles</c>.</summary>
    public Severity? AzureSeverity(string roleDefinitionId)
    {
        var role = AzureRole(roleDefinitionId);
        var listed = role is null ? null : Privilege.AzureSeverityFor(role);
        return listed ?? (Options.AllRoles ? Severity.Medium : null);
    }

    public AzureRoleDefinitionRecord? AzureRole(string roleDefinitionId) =>
        AzureRoles.TryGetValue(AzureCollector.ScopeDisplayName(roleDefinitionId), out var role) ? role : null;

    public PrincipalRecord PrincipalRecordOf(string id)
    {
        if (Principals.TryGetValue(id, out var p))
        {
            return p;
        }

        if (Groups.TryGetValue(id, out var g))
        {
            return new PrincipalRecord(g.Id, PrincipalType.Group, g.DisplayName, null, false, null, null);
        }

        return new PrincipalRecord(id, PrincipalType.Unknown, null, null, false, null, null);
    }

    public FindingPrincipal Principal(string id) => ToFinding(PrincipalRecordOf(id));

    public static FindingPrincipal ToFinding(PrincipalRecord p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new FindingPrincipal(p.Id, p.DisplayName ?? p.UserPrincipalName ?? $"<unknown principal {p.Id}>", p.UserPrincipalName, p.Type, p.IsGuest, p.AccountEnabled);
    }

    public FindingRole EntraRole(string roleDefinitionId) =>
        EntraRoles.TryGetValue(roleDefinitionId, out var r)
            ? new FindingRole(r.Id, r.DisplayName, r.TemplateId, r.IsPrivileged, RoleSystem.Entra)
            : new FindingRole(roleDefinitionId, roleDefinitionId, null, Options.AllRoles, RoleSystem.Entra);

    public FindingRole AzureFindingRole(string roleDefinitionId)
    {
        var role = AzureRole(roleDefinitionId);
        return new FindingRole(roleDefinitionId, role?.DisplayName ?? AzureCollector.ScopeDisplayName(roleDefinitionId), null, AzureSeverity(roleDefinitionId) is not null, RoleSystem.Azure);
    }

    public static FindingScope EntraScope(EntraAssignmentRecord a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.AppScopeId is { } app)
        {
            return new FindingScope(app, app, ScopeKind.Application);
        }

        return a.DirectoryScopeId == "/"
            ? new FindingScope("/", "Directory", ScopeKind.Directory)
            : new FindingScope(a.DirectoryScopeId, a.DirectoryScopeId.TrimStart('/').Replace("administrativeUnits/", "AU ", StringComparison.OrdinalIgnoreCase), ScopeKind.AdministrativeUnit);
    }

    public FindingScope AzureScope(string scope)
    {
        var known = Snapshot.AzureScopes.FirstOrDefault(s => s.Id.Equals(scope, StringComparison.OrdinalIgnoreCase));
        var kind = AzureCollector.ScopeKindOf(scope) switch
        {
            AzureScopeKind.ManagementGroup => ScopeKind.ManagementGroup,
            AzureScopeKind.Subscription => ScopeKind.Subscription,
            AzureScopeKind.ResourceGroup => ScopeKind.ResourceGroup,
            _ => ScopeKind.Resource,
        };
        return new FindingScope(scope, known?.DisplayName ?? AzureCollector.ScopeDisplayName(scope), kind);
    }

    public static FindingEvidence Evidence(EntraAssignmentRecord a) => new(a.Id, a.StartDateTime, a.EndDateTime, a.AssignmentType, a.MemberType);

    public static FindingEvidence Evidence(AzureAssignmentRecord a) => new(a.Id, a.StartDateTime, a.EndDateTime, a.AssignmentType, a.FromSchedule ? "Schedule" : "Classic");

    /// <summary>Permanent privileged Entra assignments (not the per-member echoes of a group assignment) and who holds them.</summary>
    public IEnumerable<EntraHolder> PermanentEntraHolders()
    {
        foreach (var a in Snapshot.EntraAssignments.Where(a => a.IsPermanent && !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            var principal = PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                foreach (var m in Expansion.Expand(a.PrincipalId))
                {
                    yield return new EntraHolder(a, PrincipalRecordOf(m.PrincipalId), m.Via);
                }
            }
            else
            {
                yield return new EntraHolder(a, principal, []);
            }
        }
    }

    /// <summary>The group principals of permanent privileged Entra assignments, one per assignment.</summary>
    public IEnumerable<(EntraAssignmentRecord Assignment, GroupRecord? Group, PrincipalRecord Principal)> PermanentEntraGroupAssignments()
    {
        foreach (var a in Snapshot.EntraAssignments.Where(a => a.IsPermanent && !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            var principal = PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                yield return (a, Groups.GetValueOrDefault(a.PrincipalId), principal);
            }
        }
    }

    /// <summary>
    /// Permanent Azure assignments: a classic assignment with no <c>Activated</c> schedule instance behind it
    /// (and no time-bound <c>Assigned</c> one), or a schedule-only <c>Assigned</c> instance without an end.
    /// </summary>
    public IEnumerable<AzureAssignmentRecord> PermanentAzureAssignments()
    {
        static string Key(AzureAssignmentRecord a) => $"{a.Scope}|{a.RoleDefinitionId}|{a.PrincipalId}".ToUpperInvariant();
        var schedules = Snapshot.AzureAssignments.Where(a => a.FromSchedule).ToLookup(Key);
        var classicKeys = new HashSet<string>(Snapshot.AzureAssignments.Where(a => !a.FromSchedule).Select(Key));
        foreach (var classic in Snapshot.AzureAssignments.Where(a => !a.FromSchedule))
        {
            var behind = schedules[Key(classic)].ToList();
            if (behind.Any(s => s.AssignmentType == AssignmentType.Activated) || behind.Any(s => s.AssignmentType == AssignmentType.Assigned && s.EndDateTime is not null))
            {
                continue;
            }

            yield return classic;
        }

        foreach (var schedule in Snapshot.AzureAssignments.Where(a => a.FromSchedule && a.AssignmentType == AssignmentType.Assigned && a.EndDateTime is null && !classicKeys.Contains(Key(a))))
        {
            yield return schedule;
        }
    }

    public IEnumerable<AzureHolder> PermanentAzureHolders()
    {
        foreach (var a in PermanentAzureAssignments())
        {
            if (AzureSeverity(a.RoleDefinitionId) is not { } severity)
            {
                continue;
            }

            var principal = PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                foreach (var m in Expansion.Expand(a.PrincipalId))
                {
                    yield return new AzureHolder(a, PrincipalRecordOf(m.PrincipalId), m.Via, severity);
                }
            }
            else
            {
                yield return new AzureHolder(a, principal, [], severity);
            }
        }
    }
}
```

`audit/src/Elevate.Audit/Rules/RuleRunner.cs`:

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public static class RuleRunner
{
    /// <summary>Every rule, in report order. Tasks 11–13 add theirs here.</summary>
    public static IReadOnlyList<IRule> All { get; } =
    [
    ];

    public static IReadOnlyList<Finding> Run(Snapshot snapshot, AuditOptions options, IEnumerable<IRule>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var context = new RuleContext(snapshot, options);
        var ignored = new HashSet<string>(options.IgnoredRules, StringComparer.OrdinalIgnoreCase);
        return (rules ?? All)
            .Where(r => !ignored.Contains(r.Code))
            .SelectMany(r => r.Evaluate(context))
            .OrderBy(f => Severities.Rank(f.Severity))
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Principal.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Scope.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<Finding> Visible(IReadOnlyList<Finding> findings, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(options);
        return findings.Where(f => Severities.Rank(f.Severity) <= Severities.Rank(options.MinSeverity)).ToList();
    }

    public static bool HasHigh(IEnumerable<Finding> findings) => findings.Any(f => f.Severity == Severity.High);
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: everything passes except the `RuleRunner.All … HaveCount(10)` assertion, which stays red until Task 13.

- [ ] **Step 6: Commit**

```bash
git add audit
git commit -m "audit: rule context, group expansion, privilege list and runner (All fills in through task 13)"
```

---

### Task 11: Entra rules

**Files:**
- Create: `audit/src/Elevate.Audit/Rules/EntraRules.cs`
- Modify: `audit/src/Elevate.Audit/Rules/RuleRunner.cs` (register)
- Test: `audit/tests/Elevate.Audit.Tests/EntraRulesTests.cs`

**Interfaces:**
- Produces: `EntraUserPermanentRule` (`ENTRA-USER-PERMANENT`), `EntraGroupPermanentRule` (`ENTRA-GROUP-PERMANENT`), `EntraGroupNotPimRule` (`ENTRA-GROUP-NOT-PIM`), `EntraGroupNotAssignableRule` (`ENTRA-GROUP-NOT-ASSIGNABLE`).

- [ ] **Step 1: Write the failing tests**

```csharp
using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class EntraRulesTests
{
    private static IReadOnlyList<Finding> Run(Snapshot snapshot, AuditOptions? options = null) =>
        RuleRunner.Run(snapshot, options ?? new AuditOptions(), [new EntraUserPermanentRule(), new EntraGroupPermanentRule(), new EntraGroupNotPimRule(), new EntraGroupNotAssignableRule()]);

    [Fact]
    public void DirectPermanentUser_IsHigh_ActivatedAndTimeBoundAreNot()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .User("u3", "Priya Natarajan", "priya.natarajan@contoso.com")
            .Assigned("a1", "u1", "rd-ga")
            .Assigned("a2", "u2", "rd-ga", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T16:00:00Z"))
            .Assigned("a3", "u3", "rd-ga", end: DateTimeOffset.Parse("2026-12-31T00:00:00Z"))
            .Assigned("a4", "u1", "rd-reader")
            .Build();

        var findings = Run(snapshot);

        var f = findings.Should().ContainSingle().Subject;
        f.Id.Should().Be("ENTRA-USER-PERMANENT");
        f.Severity.Should().Be(Severity.High);
        f.Principal.DisplayName.Should().Be("Sam Chen");
        f.Role.DisplayName.Should().Be("Global Administrator");
        f.Scope.Kind.Should().Be(ScopeKind.Directory);
        f.Via.Should().BeEmpty();
        f.Remedy.Should().Contain("eligible");
        f.PortalUrl.Should().StartWith("https://entra.microsoft.com/");
        f.Evidence.AssignmentId.Should().Be("a1");
    }

    [Fact]
    public void AllRoles_IncludesNonPrivilegedRoles()
    {
        var snapshot = SnapshotBuilder.Contoso().User("u1", "Sam Chen", "sam.chen@contoso.com").Assigned("a4", "u1", "rd-reader").Build();

        Run(snapshot, new AuditOptions(AllRoles: true)).Should().ContainSingle().Which.Role.DisplayName.Should().Be("Global Reader");
    }

    [Fact]
    public void PermanentGroup_YieldsTheGroupAndEachNestedUserWithItsPath()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .Group("g1", "Tier 0 Admins", pim: PimStatus.Onboarded, members: [("u1", PrincipalType.User), ("g2", PrincipalType.Group)])
            .Group("g2", "Platform Team", assignable: false, members: [("u2", PrincipalType.User), ("g1", PrincipalType.Group)])
            .Assigned("a1", "g1", "rd-pra")
            .Assigned("a1-u1", "u1", "rd-pra", memberType: "Group")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ENTRA-GROUP-PERMANENT").ToList();

        findings.Should().HaveCount(3);
        findings.Should().ContainSingle(f => f.Principal.Type == PrincipalType.Group).Which.Principal.DisplayName.Should().Be("Tier 0 Admins");
        findings.Single(f => f.Principal.Id == "u1").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins");
        findings.Single(f => f.Principal.Id == "u2").Via.Select(v => v.DisplayName).Should().Equal("Tier 0 Admins", "Platform Team");
        Run(snapshot).Should().NotContain(f => f.Id == "ENTRA-USER-PERMANENT", "the per-member echo (memberType Group) is not a direct assignment");
    }

    [Fact]
    public void GroupNotOnboarded_AndNotAssignable_AreMedium()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .Group("g1", "Tier 0 Admins", pim: PimStatus.NotOnboarded)
            .Group("g2", "Legacy Ops", assignable: false, pim: PimStatus.NotOnboarded)
            .Group("g3", "Fine", pim: PimStatus.Onboarded)
            .Assigned("a1", "g1", "rd-ga")
            .Assigned("a2", "g2", "rd-ga")
            .Assigned("a3", "g3", "rd-ga")
            .Build();

        var findings = Run(snapshot);

        findings.Should().ContainSingle(f => f.Id == "ENTRA-GROUP-NOT-PIM").Which.Principal.Id.Should().Be("g1");
        findings.Should().ContainSingle(f => f.Id == "ENTRA-GROUP-NOT-ASSIGNABLE").Which.Principal.Id.Should().Be("g2");
        findings.Where(f => f.Id is "ENTRA-GROUP-NOT-PIM" or "ENTRA-GROUP-NOT-ASSIGNABLE").Should().OnlyContain(f => f.Severity == Severity.Medium);
    }

    [Fact]
    public void DynamicGroup_RemedyMentionsIt()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .Group("g1", "All Engineers", dynamic: true, members: [])
            .Assigned("a1", "g1", "rd-ga")
            .Build();

        Run(snapshot).Single(f => f.Id == "ENTRA-GROUP-PERMANENT").Remedy.Should().Contain("dynamic");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

`audit/src/Elevate.Audit/Rules/EntraRules.cs`:

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public sealed class EntraUserPermanentRule : IRule
{
    public string Code => "ENTRA-USER-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var h in context.PermanentEntraHolders().Where(h => h.Via.Count == 0 && h.Principal.Type == PrincipalType.User))
        {
            var role = context.EntraRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(
                Code,
                Severity.High,
                RuleContext.ToFinding(h.Principal),
                role,
                RuleContext.EntraScope(h.Assignment),
                [],
                $"Remove the permanent {role.DisplayName} assignment and make {h.Principal.DisplayName ?? h.Principal.Id} eligible for it in PIM.",
                PortalLinks.EntraRoles,
                RuleContext.Evidence(h.Assignment));
        }
    }
}

public sealed class EntraGroupPermanentRule : IRule
{
    public string Code => "ENTRA-GROUP-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var (assignment, group, principal) in context.PermanentEntraGroupAssignments())
        {
            var role = context.EntraRole(assignment.RoleDefinitionId);
            var name = principal.DisplayName ?? principal.Id;
            var remedy = group is { IsDynamic: true }
                ? $"{name} is a dynamic group, which PIM for Groups cannot govern. Move the {role.DisplayName} assignment to a static role-assignable group and make its members eligible."
                : $"Make {name}'s {role.DisplayName} assignment eligible, or keep it active and make the group's members eligible through PIM for Groups.";
            yield return new Finding(Code, Severity.High, RuleContext.ToFinding(principal), role, RuleContext.EntraScope(assignment), [], remedy, PortalLinks.Group(principal.Id), RuleContext.Evidence(assignment));

            foreach (var m in context.Expansion.Expand(assignment.PrincipalId).Where(m => m.Type == PrincipalType.User))
            {
                var member = context.PrincipalRecordOf(m.PrincipalId);
                yield return new Finding(
                    Code,
                    Severity.High,
                    RuleContext.ToFinding(member),
                    role,
                    RuleContext.EntraScope(assignment),
                    m.Via,
                    $"{member.DisplayName ?? member.Id} holds {role.DisplayName} permanently through {string.Join(" ← ", m.Via.Select(v => v.DisplayName))}. Make the membership or the group's assignment eligible.",
                    PortalLinks.Group(assignment.PrincipalId),
                    RuleContext.Evidence(assignment));
            }
        }
    }
}

public sealed class EntraGroupNotPimRule : IRule
{
    public string Code => "ENTRA-GROUP-NOT-PIM";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in context.Snapshot.EntraAssignments.Where(a => !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && context.IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            if (!context.Groups.TryGetValue(a.PrincipalId, out var group) || !group.IsAssignableToRole || group.PimStatus != PimStatus.NotOnboarded || !seen.Add(group.Id))
            {
                continue;
            }

            yield return new Finding(
                Code,
                Severity.Medium,
                context.Principal(group.Id),
                context.EntraRole(a.RoleDefinitionId),
                RuleContext.EntraScope(a),
                [],
                $"Onboard {group.DisplayName} to PIM for Groups so its membership can be eligible instead of permanent.",
                PortalLinks.GroupPim(group.Id),
                RuleContext.Evidence(a));
        }
    }
}

public sealed class EntraGroupNotAssignableRule : IRule
{
    public string Code => "ENTRA-GROUP-NOT-ASSIGNABLE";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in context.Snapshot.EntraAssignments.Where(a => !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase) && context.IsPrivilegedEntra(a.RoleDefinitionId)))
        {
            if (!context.Groups.TryGetValue(a.PrincipalId, out var group) || group.IsAssignableToRole || !seen.Add(group.Id))
            {
                continue;
            }

            yield return new Finding(
                Code,
                Severity.Medium,
                context.Principal(group.Id),
                context.EntraRole(a.RoleDefinitionId),
                RuleContext.EntraScope(a),
                [],
                $"{group.DisplayName} is not role-assignable, so PIM for Groups cannot govern it. Recreate it as a role-assignable group and move the assignment.",
                PortalLinks.Group(group.Id),
                RuleContext.Evidence(a));
        }
    }
}
```

Register in `RuleRunner.All`: `new EntraUserPermanentRule(), new EntraGroupPermanentRule(), new EntraGroupNotPimRule(), new EntraGroupNotAssignableRule(),`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: the new tests pass; only the count-of-ten assertion remains red.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: Entra permanent-assignment and group hygiene rules"
```

---

### Task 12: PIM for Groups, guest and service principal rules

**Files:**
- Create: `audit/src/Elevate.Audit/Rules/GroupAndPrincipalRules.cs`
- Modify: `audit/src/Elevate.Audit/Rules/RuleRunner.cs`
- Test: `audit/tests/Elevate.Audit.Tests/GroupAndPrincipalRulesTests.cs`

**Interfaces:**
- Produces: `GroupMemberPermanentRule` (`GROUP-MEMBER-PERMANENT`), `GuestPermanentRule` (`GUEST-PERMANENT`), `ServicePrincipalPermanentRule` (`SP-PERMANENT`).

- [ ] **Step 1: Write the failing tests**

```csharp
using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class GroupAndPrincipalRulesTests
{
    private static IReadOnlyList<Finding> Run(Snapshot snapshot) =>
        RuleRunner.Run(snapshot, new AuditOptions(), [new GroupMemberPermanentRule(), new GuestPermanentRule(), new ServicePrincipalPermanentRule()]);

    [Fact]
    public void OnboardedGroupWithPermanentMemberOrOwner_IsHigh()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .User("u3", "Priya Natarajan", "priya.natarajan@contoso.com")
            .Group("g1", "Tier 0 Admins")
            .GroupPim("g1", "p1", "u1", "member")
            .GroupPim("g1", "p2", "u2", "owner")
            .GroupPim("g1", "p3", "u3", "member", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T18:00:00Z"))
            .GroupPim("g1", "p4", "u3", "member", eligible: true)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "GROUP-MEMBER-PERMANENT").ToList();

        findings.Should().HaveCount(2);
        findings.Should().OnlyContain(f => f.Severity == Severity.High && f.Role.System == RoleSystem.Group && f.Scope.Kind == ScopeKind.Group);
        findings.Single(f => f.Principal.Id == "u2").Role.DisplayName.Should().Be("Tier 0 Admins (owner)");
        findings.Single(f => f.Principal.Id == "u1").PortalUrl.Should().Contain("aadgroup");
    }

    [Fact]
    public void Guests_AreFlaggedDirectlyAndThroughGroups_ForEntraAndAzure()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("guest1", "Priya Natarajan", "priya_fabrikam.com#EXT#@contoso.com", guest: true)
            .User("guest2", "Casey Wong", "casey_fabrikam.com#EXT#@contoso.com", guest: true)
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .Group("g1", "Tier 0 Admins", members: [("guest2", PrincipalType.User), ("u1", PrincipalType.User)])
            .Assigned("a1", "guest1", "rd-ga")
            .Assigned("a2", "g1", "rd-ga")
            .AzureAssigned("ra1", "/subscriptions/sub1", "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "guest1", "User")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "GUEST-PERMANENT").ToList();

        findings.Should().HaveCount(3);
        findings.Where(f => f.Principal.Id == "guest1").Select(f => f.Role.System).Should().BeEquivalentTo([RoleSystem.Entra, RoleSystem.Azure]);
        findings.Single(f => f.Principal.Id == "guest2").Via.Should().ContainSingle().Which.DisplayName.Should().Be("Tier 0 Admins");
        findings.Should().OnlyContain(f => f.Severity == Severity.High && f.Principal.IsGuest);
    }

    [Fact]
    public void ServicePrincipals_AreInformational()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .ServicePrincipal("sp1", "Deploy Bot")
            .ServicePrincipal("mi1", "Backup Job", "ManagedIdentity")
            .Assigned("a1", "sp1", "rd-ga")
            .AzureAssigned("ra1", "/subscriptions/sub1", "8e3af657-a8ff-443c-a75c-2fe8c4bcb635", "mi1", "ServicePrincipal")
            .AzureAssigned("ra2", "/subscriptions/sub1", "acdd72a7-3385-48ef-bd42-f606fba81ae7", "mi1", "ServicePrincipal")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "SP-PERMANENT").ToList();

        findings.Should().HaveCount(2, "Reader is not privileged");
        findings.Should().OnlyContain(f => f.Severity == Severity.Info && f.Principal.Type == PrincipalType.ServicePrincipal);
        findings.Single(f => f.Principal.Id == "sp1").Remedy.Should().Contain("workload identity");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public sealed class GroupMemberPermanentRule : IRule
{
    public string Code => "GROUP-MEMBER-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var group in context.Snapshot.Groups.Where(g => g.PimStatus == PimStatus.Onboarded))
        {
            foreach (var p in group.PimAssignments.Where(p => p.IsPermanent))
            {
                var principal = context.PrincipalRecordOf(p.PrincipalId);
                yield return new Finding(
                    Code,
                    Severity.High,
                    RuleContext.ToFinding(principal),
                    new FindingRole(group.Id, $"{group.DisplayName} ({p.AccessId})", null, true, RoleSystem.Group),
                    new FindingScope(group.Id, group.DisplayName, ScopeKind.Group),
                    [],
                    $"{group.DisplayName} is managed by PIM for Groups, but {principal.DisplayName ?? principal.Id} is a permanent {p.AccessId}. Convert the assignment to eligible.",
                    PortalLinks.GroupPim(group.Id),
                    new FindingEvidence(p.Id, p.StartDateTime, p.EndDateTime, p.AssignmentType, null));
            }
        }
    }
}

public sealed class GuestPermanentRule : IRule
{
    public string Code => "GUEST-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var h in context.PermanentEntraHolders().Where(h => h.Principal.IsGuest))
        {
            var role = context.EntraRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.High, RuleContext.ToFinding(h.Principal), role, RuleContext.EntraScope(h.Assignment), h.Via,
                $"Guest {h.Principal.DisplayName ?? h.Principal.Id} holds {role.DisplayName} permanently{Through(h.Via)}. Guests should hold privileged roles only as eligible, if at all.",
                h.Via.Count == 0 ? PortalLinks.EntraRoles : PortalLinks.Group(h.Via[0].Id), RuleContext.Evidence(h.Assignment));
        }

        foreach (var h in context.PermanentAzureHolders().Where(h => h.Principal.IsGuest))
        {
            var role = context.AzureFindingRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.High, RuleContext.ToFinding(h.Principal), role, context.AzureScope(h.Assignment.Scope), h.Via,
                $"Guest {h.Principal.DisplayName ?? h.Principal.Id} holds {role.DisplayName} permanently{Through(h.Via)}. Guests should hold privileged roles only as eligible, if at all.",
                PortalLinks.AzureScope(h.Assignment.Scope), RuleContext.Evidence(h.Assignment));
        }
    }

    internal static string Through(IReadOnlyList<GroupRef> via) => via.Count == 0 ? string.Empty : $" through {string.Join(" ← ", via.Select(v => v.DisplayName))}";
}

public sealed class ServicePrincipalPermanentRule : IRule
{
    public string Code => "SP-PERMANENT";

    private const string RemedyTail = "PIM eligibility does not apply to workload identities; review whether the workload identity needs the role at all, scope it down, or replace it with a narrower custom role.";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var h in context.PermanentEntraHolders().Where(h => h.Principal.Type == PrincipalType.ServicePrincipal))
        {
            var role = context.EntraRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.Info, RuleContext.ToFinding(h.Principal), role, RuleContext.EntraScope(h.Assignment), h.Via,
                $"{h.Principal.DisplayName ?? h.Principal.Id} holds {role.DisplayName} permanently{GuestPermanentRule.Through(h.Via)}. {RemedyTail}",
                PortalLinks.EntraRoles, RuleContext.Evidence(h.Assignment));
        }

        foreach (var h in context.PermanentAzureHolders().Where(h => h.Principal.Type == PrincipalType.ServicePrincipal))
        {
            var role = context.AzureFindingRole(h.Assignment.RoleDefinitionId);
            yield return new Finding(Code, Severity.Info, RuleContext.ToFinding(h.Principal), role, context.AzureScope(h.Assignment.Scope), h.Via,
                $"{h.Principal.DisplayName ?? h.Principal.Id} holds {role.DisplayName} permanently{GuestPermanentRule.Through(h.Via)}. {RemedyTail}",
                PortalLinks.AzureScope(h.Assignment.Scope), RuleContext.Evidence(h.Assignment));
        }
    }
}
```

Register in `RuleRunner.All` after the Entra rules: `new GroupMemberPermanentRule(), new GuestPermanentRule(), new ServicePrincipalPermanentRule(),`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: new tests pass; the count-of-ten assertion still red.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: PIM for Groups, guest and service principal rules"
```

---

### Task 13: Azure and hygiene rules

**Files:**
- Create: `audit/src/Elevate.Audit/Rules/AzureAndHygieneRules.cs`
- Modify: `audit/src/Elevate.Audit/Rules/RuleRunner.cs`
- Test: `audit/tests/Elevate.Audit.Tests/AzureAndHygieneRulesTests.cs`

**Interfaces:**
- Produces: `AzurePermanentRule` (`AZURE-PERMANENT`), `EligibleNoEndRule` (`ELIGIBLE-NO-END`), `GlobalAdminCountRule` (`GA-COUNT`). After this task `RuleRunner.All` has exactly ten rules in the order: ENTRA-USER-PERMANENT, ENTRA-GROUP-PERMANENT, ENTRA-GROUP-NOT-PIM, ENTRA-GROUP-NOT-ASSIGNABLE, GROUP-MEMBER-PERMANENT, AZURE-PERMANENT, SP-PERMANENT, GUEST-PERMANENT, ELIGIBLE-NO-END, GA-COUNT.

- [ ] **Step 1: Write the failing tests**

```csharp
using Elevate.Audit.Model;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class AzureAndHygieneRulesTests
{
    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    private const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    private const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    private static IReadOnlyList<Finding> Run(Snapshot snapshot, AuditOptions? options = null) =>
        RuleRunner.Run(snapshot, options ?? new AuditOptions(), [new AzurePermanentRule(), new EligibleNoEndRule(), new GlobalAdminCountRule()]);

    [Fact]
    public void AzurePermanent_SeverityFollowsTheRole_ActivationsAreExcluded_GroupsExpand()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .User("u2", "Jordan Lee", "jordan.lee@contoso.com")
            .User("u3", "Priya Natarajan", "priya.natarajan@contoso.com")
            .Group("g1", "Cloud Ops", members: [("u3", PrincipalType.User)])
            .AzureAssigned("ra-owner", "/subscriptions/sub1", Owner, "u1", "User")
            .AzureAssigned("ra-contrib", "/subscriptions/sub1/resourceGroups/rg-app", Contributor, "g1", "Group")
            .AzureAssigned("ra-reader", "/subscriptions/sub1", Reader, "u1", "User")
            .AzureAssigned("ra-activated", "/subscriptions/sub1", Owner, "u2", "User")
            .AzureAssigned("si-activated", "/subscriptions/sub1", Owner, "u2", "User", type: AssignmentType.Activated, end: DateTimeOffset.Parse("2026-09-13T20:00:00Z"), fromSchedule: true)
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "AZURE-PERMANENT").ToList();

        findings.Should().HaveCount(3);
        findings.Single(f => f.Principal.Id == "u1").Should().BeEquivalentTo(new { Severity = Severity.High, Role = new { DisplayName = "Owner" }, Scope = new { DisplayName = "Production", Kind = ScopeKind.Subscription } });
        findings.Single(f => f.Principal.Id == "g1").Should().BeEquivalentTo(new { Severity = Severity.Medium, Scope = new { DisplayName = "rg-app", Kind = ScopeKind.ResourceGroup } });
        findings.Single(f => f.Principal.Id == "u3").Via.Select(v => v.DisplayName).Should().Equal("Cloud Ops");
        findings.Should().NotContain(f => f.Principal.Id == "u2", "an Activated schedule instance behind the classic assignment means it is a PIM activation");
        findings[0].PortalUrl.Should().StartWith("https://portal.azure.com/#@/resource/subscriptions/sub1");
    }

    [Fact]
    public void EligibilitiesWithoutEnd_AreLow_AcrossAllThreeSystems()
    {
        var snapshot = SnapshotBuilder.Contoso()
            .User("u1", "Sam Chen", "sam.chen@contoso.com")
            .Group("g1", "Tier 0 Admins")
            .Eligible("e1", "u1", "rd-ga")
            .Eligible("e2", "u1", "rd-ga", end: DateTimeOffset.Parse("2027-01-01T00:00:00Z"))
            .Eligible("e3", "u1", "rd-reader")
            .GroupPim("g1", "ge1", "u1", eligible: true)
            .AzureEligible("ae1", "/subscriptions/sub1", Owner, "u1", "User")
            .Build();

        var findings = Run(snapshot).Where(f => f.Id == "ELIGIBLE-NO-END").ToList();

        findings.Should().HaveCount(3);
        findings.Select(f => f.Role.System).Should().BeEquivalentTo([RoleSystem.Entra, RoleSystem.Group, RoleSystem.Azure]);
        findings.Should().OnlyContain(f => f.Severity == Severity.Low);
    }

    [Fact]
    public void GlobalAdminCount_CountsDistinctPeopleThroughGroups()
    {
        var one = SnapshotBuilder.Contoso().User("u1", "Sam Chen", "sam.chen@contoso.com").Assigned("a1", "u1", "rd-ga").Build();
        var six = SnapshotBuilder.Contoso()
            .User("u1", "A", "a@contoso.com").User("u2", "B", "b@contoso.com").User("u3", "C", "c@contoso.com")
            .User("u4", "D", "d@contoso.com").User("u5", "E", "e@contoso.com").User("u6", "F", "f@contoso.com")
            .Group("g1", "GA Group", members: [("u5", PrincipalType.User), ("u6", PrincipalType.User), ("u1", PrincipalType.User)])
            .Assigned("a1", "u1", "rd-ga").Assigned("a2", "u2", "rd-ga").Eligible("e3", "u3", "rd-ga").Eligible("e4", "u4", "rd-ga")
            .Assigned("a5", "g1", "rd-ga")
            .Build();
        var three = SnapshotBuilder.Contoso()
            .User("u1", "A", "a@contoso.com").User("u2", "B", "b@contoso.com").User("u3", "C", "c@contoso.com")
            .Assigned("a1", "u1", "rd-ga").Eligible("e2", "u2", "rd-ga").Eligible("e3", "u3", "rd-ga")
            .Build();

        Run(one).Should().ContainSingle(f => f.Id == "GA-COUNT").Which.Remedy.Should().Contain("1 ");
        Run(six).Should().ContainSingle(f => f.Id == "GA-COUNT").Which.Remedy.Should().Contain("6 ");
        Run(three).Should().NotContain(f => f.Id == "GA-COUNT");
        Run(one).Single(f => f.Id == "GA-COUNT").Should().BeEquivalentTo(new { Severity = Severity.Medium, Principal = new { Id = "11111111-1111-1111-1111-111111111111", DisplayName = "Contoso" } });
    }

    [Fact]
    public void All_HasTenRulesInReportOrder()
    {
        RuleRunner.All.Select(r => r.Code).Should().Equal(
            "ENTRA-USER-PERMANENT", "ENTRA-GROUP-PERMANENT", "ENTRA-GROUP-NOT-PIM", "ENTRA-GROUP-NOT-ASSIGNABLE",
            "GROUP-MEMBER-PERMANENT", "AZURE-PERMANENT", "SP-PERMANENT", "GUEST-PERMANENT", "ELIGIBLE-NO-END", "GA-COUNT");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Rules;

public sealed class AzurePermanentRule : IRule
{
    public string Code => "AZURE-PERMANENT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var a in context.PermanentAzureAssignments())
        {
            if (context.AzureSeverity(a.RoleDefinitionId) is not { } severity)
            {
                continue;
            }

            var role = context.AzureFindingRole(a.RoleDefinitionId);
            var scope = context.AzureScope(a.Scope);
            var principal = context.PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group || string.Equals(a.PrincipalType, "Group", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Finding(Code, severity, RuleContext.ToFinding(principal), role, scope, [],
                    $"Make the group's {role.DisplayName} assignment on {scope.DisplayName} eligible in PIM for Azure resources, or govern the group's membership with PIM for Groups.",
                    PortalLinks.AzureScope(a.Scope), RuleContext.Evidence(a));
                foreach (var m in context.Expansion.Expand(a.PrincipalId).Where(m => m.Type == PrincipalType.User))
                {
                    var member = context.PrincipalRecordOf(m.PrincipalId);
                    yield return new Finding(Code, severity, RuleContext.ToFinding(member), role, scope, m.Via,
                        $"{member.DisplayName ?? member.Id} holds {role.DisplayName} on {scope.DisplayName} permanently through {string.Join(" ← ", m.Via.Select(v => v.DisplayName))}.",
                        PortalLinks.AzureScope(a.Scope), RuleContext.Evidence(a));
                }
            }
            else if (principal.Type == PrincipalType.User)
            {
                yield return new Finding(Code, severity, RuleContext.ToFinding(principal), role, scope, [],
                    $"Remove the permanent {role.DisplayName} assignment on {scope.DisplayName} and make {principal.DisplayName ?? principal.Id} eligible for it in PIM.",
                    PortalLinks.AzureScope(a.Scope), RuleContext.Evidence(a));
            }
        }
    }
}

public sealed class EligibleNoEndRule : IRule
{
    public string Code => "ELIGIBLE-NO-END";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string remedy = "Give the eligibility an end date so it is reviewed, or cover it with an access review.";
        foreach (var e in context.Snapshot.EntraEligibilities.Where(e => e.EndDateTime is null && context.IsPrivilegedEntra(e.RoleDefinitionId)))
        {
            yield return new Finding(Code, Severity.Low, context.Principal(e.PrincipalId), context.EntraRole(e.RoleDefinitionId), RuleContext.EntraScope(e), [], remedy, PortalLinks.EntraRoles, RuleContext.Evidence(e));
        }

        foreach (var g in context.Snapshot.Groups)
        {
            foreach (var e in g.PimEligibilities.Where(e => e.EndDateTime is null))
            {
                yield return new Finding(Code, Severity.Low, context.Principal(e.PrincipalId), new FindingRole(g.Id, $"{g.DisplayName} ({e.AccessId})", null, true, RoleSystem.Group),
                    new FindingScope(g.Id, g.DisplayName, ScopeKind.Group), [], remedy, PortalLinks.GroupPim(g.Id), new FindingEvidence(e.Id, e.StartDateTime, e.EndDateTime, null, null));
            }
        }

        foreach (var e in context.Snapshot.AzureEligibilities.Where(e => e.EndDateTime is null && context.AzureSeverity(e.RoleDefinitionId) is not null))
        {
            yield return new Finding(Code, Severity.Low, context.Principal(e.PrincipalId), context.AzureFindingRole(e.RoleDefinitionId), context.AzureScope(e.Scope), [], remedy, PortalLinks.AzureScope(e.Scope), RuleContext.Evidence(e));
        }
    }
}

public sealed class GlobalAdminCountRule : IRule
{
    public const string GlobalAdministratorTemplate = "62e90394-69f5-4237-9190-012177145e10";

    public string Code => "GA-COUNT";

    public IEnumerable<Finding> Evaluate(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var gaRoleIds = context.EntraRoles.Values.Where(r => string.Equals(r.TemplateId, GlobalAdministratorTemplate, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gaRoleIds.Count == 0)
        {
            yield break;
        }

        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = context.Snapshot.EntraAssignments.Where(a => a.IsPermanent && !string.Equals(a.MemberType, "Group", StringComparison.OrdinalIgnoreCase))
            .Concat(context.Snapshot.EntraEligibilities)
            .Where(a => gaRoleIds.Contains(a.RoleDefinitionId) && a.DirectoryScopeId == "/");
        foreach (var a in records)
        {
            var principal = context.PrincipalRecordOf(a.PrincipalId);
            if (principal.Type == PrincipalType.Group)
            {
                foreach (var m in context.Expansion.Expand(a.PrincipalId).Where(m => m.Type == PrincipalType.User))
                {
                    people.Add(m.PrincipalId);
                }
            }
            else if (principal.Type == PrincipalType.User)
            {
                people.Add(principal.Id);
            }
        }

        if (people.Count is >= 2 and <= 5)
        {
            yield break;
        }

        var tenant = context.Snapshot.Tenant;
        var roleId = gaRoleIds.First();
        yield return new Finding(
            Code,
            Severity.Medium,
            new FindingPrincipal(tenant.Id, tenant.DisplayName ?? tenant.Id, null, PrincipalType.Unknown, false, null),
            context.EntraRole(roleId),
            new FindingScope("/", "Directory", ScopeKind.Directory),
            [],
            people.Count < 2
                ? $"{people.Count} person can become Global Administrator. Microsoft recommends at least two (for break-glass) and at most five."
                : $"{people.Count} people can become Global Administrator (permanent or eligible). Microsoft recommends at most five; move the rest to narrower roles.",
            PortalLinks.EntraRoles,
            new FindingEvidence("global-administrator-count", null, null, null, null));
    }
}
```

Register in `RuleRunner.All` so the final order is exactly the ten codes listed in the Interfaces block: insert `new AzurePermanentRule()` after `GroupMemberPermanentRule`, and append `new EligibleNoEndRule(), new GlobalAdminCountRule()`.

- [ ] **Step 4: Run the tests to verify they all pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: everything green, including the count-of-ten assertion from Task 10.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: Azure permanent, eligibility-without-end and Global Administrator count rules"
```

---

### Task 14: JSON report, snapshot file, the sample tenant and golden support

**Files:**
- Create: `audit/src/Elevate.Audit/Rendering/AuditReport.cs`, `audit/src/Elevate.Audit/Rendering/JsonRenderer.cs`, `audit/src/Elevate.Audit/Rendering/SnapshotFile.cs`
- Create: `audit/tests/Elevate.Audit.Tests/Support/Repo.cs`, `audit/tests/Elevate.Audit.Tests/Support/Golden.cs`, `audit/tests/Elevate.Audit.Tests/Support/SampleSnapshot.cs`, `audit/tests/Elevate.Audit.Tests/JsonAndSnapshotTests.cs`
- Generated goldens: `audit/tests/Elevate.Audit.Tests/Fixtures/snapshots/sample.json`, `audit/tests/Elevate.Audit.Tests/Golden/sample-report.json`

**Interfaces:**
- Produces: `AuditReport` record with `static AuditReport From(Snapshot snapshot, IReadOnlyList<Finding> findings, AuditOptions options, string toolVersion, IReadOnlyList<string> scopesRequested)`; `JsonRenderer.Render(AuditReport): string`; `SnapshotFile.Save(Snapshot, string path)`, `SnapshotFile.Load(string path): Snapshot`; test `SampleSnapshot.Build(): Snapshot`, `Golden.Check(string repoRelativePath, string actual)`, `Repo.Root: string`.

- [ ] **Step 1: Test support**

`audit/tests/Elevate.Audit.Tests/Support/Repo.cs`:

```csharp
namespace Elevate.Audit.Tests.Support;

public static class Repo
{
    /// <summary>The repository root: the nearest ancestor of the test binary that contains audit/Elevate.Audit.sln.</summary>
    public static string Root { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "audit", "Elevate.Audit.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
```

`audit/tests/Elevate.Audit.Tests/Support/Golden.cs`:

```csharp
using FluentAssertions;

namespace Elevate.Audit.Tests.Support;

/// <summary>Golden-file comparison. Set ELEVATE_AUDIT_UPDATE_GOLDEN=1 to rewrite the expected files instead of comparing.</summary>
public static class Golden
{
    public static void Check(string repoRelativePath, string actual)
    {
        var path = Path.Combine(Repo.Root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var normalized = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("ELEVATE_AUDIT_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalized);
            return;
        }

        File.Exists(path).Should().BeTrue($"{repoRelativePath} is missing; run: ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized.Should().Be(expected, $"{repoRelativePath} is stale; regenerate with: ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln");
    }
}
```

`audit/tests/Elevate.Audit.Tests/Support/SampleSnapshot.cs` — the fictional tenant that drives the committed sample report. Every rule fires at least once:

```csharp
using Elevate.Audit.Model;

namespace Elevate.Audit.Tests.Support;

public static class SampleSnapshot
{
    public const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    public const string Contributor = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    public const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    public static Snapshot Build() => SnapshotBuilder.Contoso()
        .Tenant("72f988bf-0000-4000-8000-2d7cd011db47", "Contoso")
        .EntraRole("rd-sec", "194ae4cb-b126-40b2-bd5b-6091b380977d", "Security Administrator", privileged: true)
        .User("u-alex", "Alex Rivera", "alex.rivera@contoso.com")
        .User("u-sam", "Sam Chen", "sam.chen@contoso.com")
        .User("u-priya", "Priya Natarajan", "priya.natarajan_fabrikam.com#EXT#@contoso.com", guest: true)
        .User("u-jordan", "Jordan Lee", "jordan.lee@contoso.com")
        .User("u-casey", "Casey Wong", "casey.wong@contoso.com")
        .User("u-riley", "Riley Park", "riley.park@contoso.com")
        .User("u-morgan", "Morgan Diaz", "morgan.diaz@contoso.com")
        .ServicePrincipal("sp-deploy", "Deploy Bot")
        .Group("g-tier0", "Tier 0 Admins", pim: PimStatus.Onboarded, members: [("u-sam", PrincipalType.User), ("g-platform", PrincipalType.Group)])
        .Group("g-platform", "Platform Team", assignable: false, members: [("u-casey", PrincipalType.User), ("g-tier0", PrincipalType.Group)])
        .Group("g-legacy", "Legacy Ops", assignable: false, pim: PimStatus.NotOnboarded, members: [("u-riley", PrincipalType.User)])
        .Group("g-helpdesk", "Helpdesk Leads", pim: PimStatus.NotOnboarded, members: [("u-morgan", PrincipalType.User)])
        .Group("g-cloudops", "Cloud Ops", assignable: false, members: [("u-jordan", PrincipalType.User)])
        .Assigned("a-alex-ga", "u-alex", "rd-ga")
        .Assigned("a-tier0-ga", "g-tier0", "rd-ga")
        .Assigned("a-tier0-ga-sam", "u-sam", "rd-ga", memberType: "Group")
        .Assigned("a-legacy-sec", "g-legacy", "rd-sec")
        .Assigned("a-helpdesk-pra", "g-helpdesk", "rd-pra")
        .Assigned("a-priya-sec", "u-priya", "rd-sec")
        .Assigned("a-deploy-ga", "sp-deploy", "rd-ga")
        .Assigned("a-jordan-ga-active", "u-jordan", "rd-ga", type: AssignmentType.Activated, end: new DateTimeOffset(2026, 9, 13, 20, 0, 0, TimeSpan.Zero))
        .Assigned("a-alex-reader", "u-alex", "rd-reader")
        .Eligible("e-jordan-ga", "u-jordan", "rd-ga")
        .Eligible("e-casey-ga", "u-casey", "rd-ga", end: new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero))
        .Eligible("e-riley-ga", "u-riley", "rd-ga")
        .Eligible("e-morgan-ga", "u-morgan", "rd-ga")
        .GroupPim("g-tier0", "gp-sam-owner", "u-sam", "owner")
        .GroupPim("g-tier0", "gp-alex-member", "u-alex", "member", eligible: true)
        .AzureScope("/subscriptions/sub1/resourceGroups/rg-payments", AzureScopeKind.ResourceGroup, "rg-payments")
        .AzureAssigned("ra-alex-owner", "/subscriptions/sub1", Owner, "u-alex", "User")
        .AzureAssigned("ra-cloudops-contrib", "/subscriptions/sub1/resourceGroups/rg-payments", Contributor, "g-cloudops", "Group")
        .AzureAssigned("ra-deploy-owner", "/subscriptions/sub1", Owner, "sp-deploy", "ServicePrincipal")
        .AzureAssigned("ra-sam-owner-classic", "/subscriptions/sub1", Owner, "u-sam", "User")
        .AzureAssigned("si-sam-owner-active", "/subscriptions/sub1", Owner, "u-sam", "User", type: AssignmentType.Activated, end: new DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.Zero), fromSchedule: true)
        .AzureAssigned("ra-jordan-reader", "/subscriptions/sub1", Reader, "u-jordan", "User")
        .AzureEligible("ae-jordan-owner", "/subscriptions/sub1", Owner, "u-jordan", "User")
        .Skipped("azure-management-groups", "Azure management groups are not readable by this account; scanned subscriptions only.")
        .Build();
}
```

- [ ] **Step 2: Write the failing tests**

`audit/tests/Elevate.Audit.Tests/JsonAndSnapshotTests.cs`:

```csharp
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
        var report = AuditReport.From(snapshot, findings, options, "sample", Elevate.Audit.Auth.ClientIds.GraphReadScopeNames);

        var json = JsonRenderer.Render(report);

        report.Summary.Should().BeEquivalentTo(new { High = 12, Medium = 5, Low = 5, Info = 2 });
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
        File.WriteAllText(reportPath, JsonRenderer.Render(AuditReport.From(snapshot, [], new AuditOptions(), "x", [])));

        SnapshotFile.Load(snapshotPath).Should().BeEquivalentTo(snapshot, o => o.Excluding(ctx => ctx.Path.EndsWith("IsPermanent", StringComparison.Ordinal)));
        var act = () => SnapshotFile.Load(reportPath);
        act.Should().Throw<AuditException>().WithMessage("*report, not a snapshot*");
        var missing = () => SnapshotFile.Load(Path.Combine(dir.FullName, "nope.json"));
        missing.Should().Throw<AuditException>().WithMessage("*not found*");
    }
}
```

The summary numbers (12/5/5/2) are what the sample must produce (Low: three Entra eligibilities without end, one PIM for Groups eligibility, one Azure eligibility); if the implementer's count differs, list the findings, check each against the rule definitions in Tasks 11–13, fix the rule or the sample, and only then adjust the number.

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 4: Implement**

`audit/src/Elevate.Audit/Rendering/AuditReport.cs`:

```csharp
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public sealed record ReportTool(string Name, string Version);

public sealed record ReportSummary(int High, int Medium, int Low, int Info)
{
    public int Total => High + Medium + Low + Info;
}

public sealed record ReportOptions(bool AllRoles, IReadOnlyList<string> Ignored, string MinSeverity, bool SkipAzure);

/// <summary>The JSON document `--json` writes. Field names are a stability contract (golden-tested).</summary>
public sealed record AuditReport(
    ReportTool Tool,
    TenantInfo Tenant,
    string Account,
    DateTimeOffset ScannedAt,
    ReportOptions Options,
    IReadOnlyList<string> ScopesRequested,
    IReadOnlyList<SkippedSource> Skipped,
    ReportSummary Summary,
    IReadOnlyList<Finding> Findings)
{
    public static AuditReport From(Snapshot snapshot, IReadOnlyList<Finding> findings, AuditOptions options, string toolVersion, IReadOnlyList<string> scopesRequested)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(options);
        return new AuditReport(
            new ReportTool(AppInfo.Name, toolVersion),
            snapshot.Tenant,
            snapshot.Account,
            snapshot.ScannedAt,
            new ReportOptions(options.AllRoles, options.IgnoredRules, options.MinSeverity.ToString().ToLowerInvariant(), options.SkipAzure),
            scopesRequested,
            snapshot.Skipped,
            new ReportSummary(
                findings.Count(f => f.Severity == Severity.High),
                findings.Count(f => f.Severity == Severity.Medium),
                findings.Count(f => f.Severity == Severity.Low),
                findings.Count(f => f.Severity == Severity.Info)),
            findings);
    }
}
```

`audit/src/Elevate.Audit/Rendering/JsonRenderer.cs`:

```csharp
using System.Text.Json;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public static class JsonRenderer
{
    public static string Render(AuditReport report) => JsonSerializer.Serialize(report, AuditJson.Options) + "\n";
}
```

`audit/src/Elevate.Audit/Rendering/SnapshotFile.cs`:

```csharp
using System.Text.Json;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

/// <summary>`--save-snapshot` / `--from-snapshot`. The kind marker keeps a report from being mistaken for a snapshot.</summary>
public static class SnapshotFile
{
    public static void Save(Snapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, AuditJson.Options) + "\n");
    }

    public static Snapshot Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new AuditException($"Snapshot file not found: {path}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.GetString() != Snapshot.KindMarker)
        {
            throw root.ValueKind == JsonValueKind.Object && root.TryGetProperty("findings", out _)
                ? new AuditException($"{path} is a report, not a snapshot. Re-run the scan with --save-snapshot to produce one.")
                : new AuditException($"{path} is not an elevate-audit snapshot.");
        }

        return root.Deserialize<Snapshot>(AuditJson.Options) ?? throw new AuditException($"{path} is empty.");
    }
}
```

- [ ] **Step 5: Generate the goldens, then run the tests**

```bash
ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln
dotnet test audit/Elevate.Audit.sln
```

Expected: first run writes `Golden/sample-report.json` and `Fixtures/snapshots/sample.json`; second run passes without the variable. Open `sample-report.json` and read the findings once: every rule code present, every `via` path sensible, no real names.

- [ ] **Step 6: Commit**

```bash
git add audit
git commit -m "audit: JSON report, snapshot file and the Contoso sample with goldens"
```

---

### Task 15: Terminal renderer

**Files:**
- Create: `audit/src/Elevate.Audit/Rendering/TerminalRenderer.cs`, `audit/src/Elevate.Audit/Infrastructure/Terminal.cs`
- Test: `audit/tests/Elevate.Audit.Tests/TerminalRendererTests.cs`

**Interfaces:**
- Produces: `Terminal(bool quiet, bool noColor)` with `IAnsiConsole Stdout`, `IAnsiConsole Stderr`, `bool Quiet`, `void Note(string plainText)` (stderr, silent when quiet), `void Say(string plainText)` (stderr, always); `TerminalRenderer.Render(AuditReport report, IAnsiConsole console, bool summaryOnly)`; `TerminalRenderer.SummaryLine(AuditReport): string`.

- [ ] **Step 1: Write the failing test**

```csharp
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;
using Spectre.Console;

namespace Elevate.Audit.Tests;

public class TerminalRendererTests
{
    private static (IAnsiConsole Console, StringWriter Writer) PlainConsole()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 200;
        return (console, writer);
    }

    private static AuditReport Sample()
    {
        var snapshot = SampleSnapshot.Build();
        return AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "sample", ClientIds.GraphReadScopeNames);
    }

    [Fact]
    public void Render_ShowsHeaderSkippedRulesAndPaths()
    {
        var (console, writer) = PlainConsole();

        TerminalRenderer.Render(Sample(), console, summaryOnly: false);
        var text = writer.ToString();

        text.Should().Contain("Contoso").And.Contain("alex.rivera@contoso.com");
        text.Should().Contain("Skipped").And.Contain("management groups");
        text.Should().Contain("ENTRA-GROUP-PERMANENT").And.Contain("Tier 0 Admins ← Platform Team");
        text.Should().Contain("GA-COUNT");
        text.Should().Contain(TerminalRenderer.SummaryLine(Sample()));
    }

    [Fact]
    public void SummaryOnly_PrintsOneLine()
    {
        var (console, writer) = PlainConsole();

        TerminalRenderer.Render(Sample(), console, summaryOnly: true);

        writer.ToString().Trim().Split('\n').Should().ContainSingle().Which.Should().Contain("12 high").And.Contain("5 medium");
    }

    [Fact]
    public void Render_WithNoFindings_SaysSo()
    {
        var (console, writer) = PlainConsole();
        var snapshot = SnapshotBuilder.Contoso().Build();

        TerminalRenderer.Render(AuditReport.From(snapshot, [], new AuditOptions(), "x", []), console, summaryOnly: false);

        writer.ToString().Should().Contain("No standing privileged access found");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

`audit/src/Elevate.Audit/Infrastructure/Terminal.cs`:

```csharp
using Spectre.Console;

namespace Elevate.Audit.Infrastructure;

/// <summary>Results on stdout, progress and prompts on stderr, so `--json -` and redirection stay clean.</summary>
public sealed class Terminal
{
    public Terminal(bool quiet, bool noColor)
    {
        Quiet = quiet;
        var colors = noColor || Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 } ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect;
        Stdout = AnsiConsole.Create(new AnsiConsoleSettings { ColorSystem = colors, Out = new AnsiConsoleOutput(Console.Out), Interactive = InteractionSupport.No });
        Stderr = AnsiConsole.Create(new AnsiConsoleSettings { ColorSystem = colors, Out = new AnsiConsoleOutput(Console.Error) });
    }

    public bool Quiet { get; }

    public IAnsiConsole Stdout { get; }

    public IAnsiConsole Stderr { get; }

    public void Note(string plainText)
    {
        if (!Quiet)
        {
            Stderr.MarkupLine($"[grey]{Markup.Escape(plainText)}[/]");
        }
    }

    public void Say(string plainText) => Stderr.MarkupLine($"[blue]{Markup.Escape(plainText)}[/]");
}
```

`audit/src/Elevate.Audit/Rendering/TerminalRenderer.cs`:

```csharp
using System.Globalization;
using Elevate.Audit.Model;
using Spectre.Console;

namespace Elevate.Audit.Rendering;

public static class TerminalRenderer
{
    public static string SummaryLine(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = report.Summary;
        return s.Total == 0
            ? "No standing privileged access found."
            : $"{s.Total} findings: {s.High} high, {s.Medium} medium, {s.Low} low, {s.Info} info.";
    }

    public static void Render(AuditReport report, IAnsiConsole console, bool summaryOnly)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(console);
        if (summaryOnly)
        {
            console.WriteLine(SummaryLine(report));
            return;
        }

        var header = new Table().Border(TableBorder.None).HideHeaders().AddColumn(string.Empty).AddColumn(string.Empty);
        header.AddRow("[grey]Tenant[/]", Markup.Escape($"{report.Tenant.DisplayName ?? "(unnamed)"} · {report.Tenant.Id}"));
        header.AddRow("[grey]Account[/]", Markup.Escape(report.Account));
        header.AddRow("[grey]Scanned[/]", Markup.Escape(report.ScannedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)));
        header.AddRow("[grey]Tool[/]", Markup.Escape($"{report.Tool.Name} {report.Tool.Version}"));
        if (report.Options.AllRoles)
        {
            header.AddRow("[grey]Roles[/]", "all roles (--all-roles)");
        }

        if (report.Options.Ignored.Count > 0)
        {
            header.AddRow("[grey]Ignored[/]", Markup.Escape(string.Join(", ", report.Options.Ignored)));
        }

        console.Write(new Panel(header).Header("elevate-audit").Border(BoxBorder.Rounded));

        if (report.Skipped.Count > 0)
        {
            var skipped = new Table().Border(TableBorder.Rounded).AddColumn("Source").AddColumn("Reason");
            foreach (var s in report.Skipped)
            {
                skipped.AddRow(Markup.Escape(s.Source), Markup.Escape(s.Reason));
            }

            console.Write(new Panel(skipped).Header("[yellow]Skipped[/]").Border(BoxBorder.Rounded));
        }

        foreach (var group in report.Findings.GroupBy(f => f.Id))
        {
            var severity = group.First().Severity;
            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn("Principal");
            table.AddColumn("Role");
            table.AddColumn("Scope");
            table.AddColumn("Via");
            table.AddColumn("Remedy");
            foreach (var f in group)
            {
                var principal = f.Principal.Type == PrincipalType.Group ? $"[bold]{Markup.Escape(f.Principal.DisplayName)}[/] [grey](group)[/]" : Markup.Escape(f.Principal.DisplayName) + (f.Principal.IsGuest ? " [yellow](guest)[/]" : string.Empty);
                table.AddRow(
                    principal,
                    Markup.Escape(f.Role.DisplayName),
                    Markup.Escape(f.Scope.DisplayName),
                    f.Via.Count == 0 ? "[grey]direct[/]" : Markup.Escape(string.Join(" ← ", f.Via.Select(v => v.DisplayName))),
                    Markup.Escape(f.Remedy));
            }

            console.Write(new Panel(table).Header($"{Colour(severity)}{Markup.Escape(group.Key)}[/] · {Markup.Escape(severity.ToString().ToLowerInvariant())} · {group.Count()}").Border(BoxBorder.Rounded));
        }

        console.WriteLine(SummaryLine(report));
    }

    private static string Colour(Severity severity) => severity switch
    {
        Severity.High => "[red bold]",
        Severity.Medium => "[yellow bold]",
        Severity.Low => "[blue]",
        _ => "[grey]",
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add audit
git commit -m "audit: terminal renderer"
```

---

### Task 16: HTML report styled like the product page

**Files:**
- Create: `audit/src/Elevate.Audit/Rendering/HtmlRenderer.cs`, `audit/src/Elevate.Audit/Rendering/report.css`
- Modify: `audit/src/Elevate.Audit/Elevate.Audit.csproj` (embed `report.css`)
- Test: `audit/tests/Elevate.Audit.Tests/HtmlRendererTests.cs`
- Generated golden: `site/audit-sample.html`

**Interfaces:**
- Produces: `HtmlRenderer.Render(AuditReport): string`; `HtmlRenderer.Stylesheet: string`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.RegularExpressions;
using Elevate.Audit.Auth;
using Elevate.Audit.Model;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Audit.Tests.Support;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public partial class HtmlRendererTests
{
    private static AuditReport Sample()
    {
        var snapshot = SampleSnapshot.Build();
        return AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "sample", ClientIds.GraphReadScopeNames);
    }

    [Fact]
    public void Render_IsSelfContained_HasOneH1_AndOnlyHttpsLinks()
    {
        var html = HtmlRenderer.Render(Sample());

        html.Should().StartWith("<!doctype html>");
        Regex.Matches(html, "<h1[ >]").Should().HaveCount(1);
        html.Should().NotContain("<img").And.NotContain("<link ").And.NotContain("src=\"http");
        Regex.Matches(html, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Should().OnlyContain(h => h.StartsWith("https://") || h.StartsWith("#"));
        html.Should().Contain("Tier 0 Admins").And.NotContain("<script src");
        html.Should().Contain("<details");
    }

    [Fact]
    public void Render_EscapesUntrustedText()
    {
        var snapshot = SnapshotBuilder.Contoso().User("u1", "<b>Evil</b>", "evil@contoso.com").Assigned("a1", "u1", "rd-ga").Build();

        var html = HtmlRenderer.Render(AuditReport.From(snapshot, RuleRunner.Run(snapshot, new AuditOptions()), new AuditOptions(), "x", []));

        html.Should().Contain("&lt;b&gt;Evil&lt;/b&gt;").And.NotContain("<b>Evil</b>");
    }

    [Fact]
    public void Stylesheet_TokensMatchTheProductPage()
    {
        var site = File.ReadAllText(Path.Combine(Repo.Root, "site", "styles.css"));

        RootBlock(HtmlRenderer.Stylesheet).Should().Be(RootBlock(site), "audit/src/Elevate.Audit/Rendering/report.css must carry the same :root tokens as site/styles.css");
    }

    [Fact]
    public void SampleReport_MatchesTheCommittedSitePage()
    {
        Golden.Check("site/audit-sample.html", HtmlRenderer.Render(Sample()));
    }

    private static string RootBlock(string css) => RootBlockPattern().Match(css).Value;

    [GeneratedRegex(@":root\s*\{[^}]*\}")]
    private static partial Regex RootBlockPattern();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: The stylesheet**

Add to the csproj's embedded resources: `<EmbeddedResource Include="Rendering\report.css" LogicalName="Elevate.Audit.Resources.report.css" />`.

`audit/src/Elevate.Audit/Rendering/report.css` — the `:root` block is copied **byte for byte** from the top of `site/styles.css` (run `sed -n '/^:root {/,/^}/p' site/styles.css` and paste); the rest is the report's own, using only those tokens:

```css
:root {
  color-scheme: light;
  --ink: #101d32;
  --muted: #526176;
  --blue: #075bd8;
  --mist: #f5f7fb;
  --line: #dce3ec;
  --navy: #0e2138;
  --radius: 28px;
  --font:
    -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
}
* { box-sizing: border-box; }
body { margin: 0; background: #fff; color: var(--ink); font-family: var(--font); -webkit-font-smoothing: antialiased; line-height: 1.5; }
a { color: var(--blue); text-decoration: none; }
a:hover { text-decoration: underline; text-underline-offset: 4px; }
.wrap { width: min(1120px, calc(100% - 48px)); margin-inline: auto; }
.report-header { background: var(--mist); border-bottom: 1px solid var(--line); padding-block: 40px 32px; }
.eyebrow { margin: 0 0 8px; color: var(--muted); font-size: 14px; letter-spacing: 0.04em; text-transform: uppercase; }
h1 { margin: 0 0 12px; font-size: clamp(32px, 4vw, 48px); letter-spacing: -0.02em; line-height: 1.1; text-wrap: balance; }
h2 { margin: 48px 0 16px; font-size: 24px; letter-spacing: -0.01em; }
h3 { margin: 0; font-size: 17px; }
.meta { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 12px 24px; margin: 0; padding: 0; list-style: none; color: var(--muted); font-size: 14px; }
.meta strong { display: block; color: var(--ink); font-weight: 600; }
.cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 16px; margin-top: 32px; }
.card { background: #fff; border: 1px solid var(--line); border-radius: 20px; padding: 20px; }
.card .count { font-size: 36px; font-weight: 700; letter-spacing: -0.02em; }
.card .label { color: var(--muted); font-size: 14px; text-transform: capitalize; }
.card.high .count { color: #b42318; }
.card.medium .count { color: #b54708; }
.card.low .count { color: var(--blue); }
.card.info .count { color: var(--muted); }
.notice { background: var(--mist); border: 1px solid var(--line); border-radius: 16px; padding: 16px 20px; margin-top: 24px; }
.notice ul { margin: 8px 0 0; padding-left: 20px; }
.start { margin: 0; padding: 0; list-style: none; display: grid; gap: 12px; }
.start li { border: 1px solid var(--line); border-radius: 16px; padding: 16px 20px; display: grid; gap: 4px; }
.start .who { font-weight: 600; }
.start .what { color: var(--muted); font-size: 14px; }
.rule { border: 1px solid var(--line); border-radius: 20px; padding: 20px 24px; margin-top: 16px; }
.rule header { display: flex; flex-wrap: wrap; align-items: baseline; gap: 12px; margin-bottom: 12px; }
.pill { display: inline-block; border-radius: 999px; padding: 2px 10px; font-size: 12px; font-weight: 600; letter-spacing: 0.02em; text-transform: uppercase; background: var(--mist); color: var(--muted); }
.pill.high { background: #fee4e2; color: #b42318; }
.pill.medium { background: #fef0c7; color: #b54708; }
.pill.low { background: #e0ecff; color: var(--blue); }
.count-pill { color: var(--muted); font-size: 14px; }
.table-scroll { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 14px; }
th, td { text-align: left; vertical-align: top; padding: 10px 12px; border-top: 1px solid var(--line); }
th { color: var(--muted); font-weight: 600; border-top: 0; }
td .muted, .muted { color: var(--muted); }
details { margin-top: 6px; }
summary { cursor: pointer; color: var(--blue); }
.path { font-size: 13px; color: var(--muted); }
.appendix { margin-top: 48px; padding-top: 24px; border-top: 1px solid var(--line); color: var(--muted); font-size: 14px; }
.appendix code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13px; color: var(--ink); }
footer { margin-block: 48px 64px; color: var(--muted); font-size: 13px; }
@media (max-width: 760px) { .wrap { width: calc(100% - 32px); } .rule { padding: 16px; } h2 { margin-top: 36px; } }
@media print { .rule { break-inside: avoid; } a { color: inherit; } summary { display: none; } details > * { display: block; } }
```

- [ ] **Step 4: The renderer**

```csharp
using System.Globalization;
using System.Net;
using System.Text;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

/// <summary>One self-contained HTML file: inline CSS from report.css, no external requests, one h1, https links only.</summary>
public static class HtmlRenderer
{
    internal const string StylesheetResource = "Elevate.Audit.Resources.report.css";

    private static readonly Lazy<string> StylesheetText = new(() =>
    {
        using var stream = typeof(HtmlRenderer).Assembly.GetManifestResourceStream(StylesheetResource) ?? throw new InvalidOperationException("report.css missing from the bundle");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public static string Stylesheet => StylesheetText.Value;

    public static string Render(AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var b = new StringBuilder(64 * 1024);
        var tenantName = report.Tenant.DisplayName ?? report.Tenant.Id;
        b.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\" />\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />\n<meta name=\"robots\" content=\"noindex\" />\n");
        b.Append("<title>").Append(E($"Standing access report — {tenantName}")).Append("</title>\n<style>\n").Append(Stylesheet).Append("\n</style>\n</head>\n<body>\n");

        b.Append("<header class=\"report-header\"><div class=\"wrap\">\n<p class=\"eyebrow\">elevate-audit · standing privileged access</p>\n");
        b.Append("<h1>").Append(E(tenantName)).Append("</h1>\n<ul class=\"meta\">\n");
        Meta(b, "Tenant id", report.Tenant.Id);
        Meta(b, "Scanned by", report.Account);
        Meta(b, "Scanned at", report.ScannedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        Meta(b, "Tool", $"{report.Tool.Name} {report.Tool.Version}");
        b.Append("</ul>\n<div class=\"cards\">\n");
        Card(b, "high", report.Summary.High);
        Card(b, "medium", report.Summary.Medium);
        Card(b, "low", report.Summary.Low);
        Card(b, "info", report.Summary.Info);
        b.Append("</div>\n");
        if (report.Skipped.Count > 0)
        {
            b.Append("<div class=\"notice\"><strong>Some sources were skipped.</strong> The report under-counts standing access in those areas.<ul>\n");
            foreach (var s in report.Skipped)
            {
                b.Append("<li><strong>").Append(E(s.Source)).Append(":</strong> ").Append(E(s.Reason)).Append("</li>\n");
            }

            b.Append("</ul></div>\n");
        }

        b.Append("</div></header>\n<main class=\"wrap\">\n");

        var high = report.Findings.Where(f => f.Severity == Severity.High).ToList();
        b.Append("<h2 id=\"start-here\">Start here</h2>\n");
        if (high.Count == 0)
        {
            b.Append("<p class=\"muted\">No high-severity findings. ").Append(E(report.Findings.Count == 0 ? "No standing privileged access was found." : "Review the medium and low findings below.")).Append("</p>\n");
        }
        else
        {
            b.Append("<ol class=\"start\">\n");
            foreach (var f in high)
            {
                b.Append("<li><span class=\"who\">").Append(E(f.Principal.DisplayName)).Append(f.Principal.IsGuest ? " <span class=\"pill\">guest</span>" : string.Empty)
                 .Append(" · ").Append(E(f.Role.DisplayName)).Append(" on ").Append(E(f.Scope.DisplayName)).Append("</span>");
                if (f.Via.Count > 0)
                {
                    b.Append("<span class=\"path\">via ").Append(E(string.Join(" ← ", f.Via.Select(v => v.DisplayName)))).Append("</span>");
                }

                b.Append("<span class=\"what\">").Append(E(f.Remedy)).Append(" <a href=\"").Append(E(f.PortalUrl)).Append("\">Open in portal</a></span></li>\n");
            }

            b.Append("</ol>\n");
        }

        b.Append("<h2 id=\"findings\">All findings</h2>\n");
        foreach (var group in report.Findings.GroupBy(f => f.Id))
        {
            var severity = group.First().Severity.ToString().ToLowerInvariant();
            b.Append("<section class=\"rule\" id=\"").Append(E(group.Key.ToLowerInvariant())).Append("\"><header><h3>").Append(E(group.Key)).Append("</h3><span class=\"pill ").Append(severity).Append("\">").Append(severity).Append("</span><span class=\"count-pill\">").Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append(group.Count() == 1 ? " finding" : " findings").Append("</span></header>\n");
            b.Append("<div class=\"table-scroll\"><table><thead><tr><th>Principal</th><th>Role</th><th>Scope</th><th>How</th><th>Remedy</th></tr></thead><tbody>\n");
            foreach (var f in group)
            {
                b.Append("<tr><td>").Append(E(f.Principal.DisplayName));
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

            b.Append("</tbody></table></div></section>\n");
        }

        b.Append("<section class=\"appendix\" id=\"appendix\"><h2>Appendix</h2>\n<p>Options: all roles ").Append(report.Options.AllRoles ? "on" : "off").Append("; minimum severity ").Append(E(report.Options.MinSeverity)).Append("; ignored rules: ").Append(report.Options.Ignored.Count == 0 ? "none" : E(string.Join(", ", report.Options.Ignored))).Append(".</p>\n");
        b.Append("<p>Read-only Microsoft Graph scopes requested: ").Append(report.ScopesRequested.Count == 0 ? "none recorded" : string.Join(", ", report.ScopesRequested.Select(s => "<code>" + E(s) + "</code>"))).Append(". Nothing was written to the tenant.</p>\n");
        b.Append("<p>This report contains personal data (names and sign-in names of people who hold roles). Handle it as you would any access review.</p>\n<details><summary>Evidence ids</summary><div class=\"table-scroll\"><table><thead><tr><th>Rule</th><th>Principal id</th><th>Assignment id</th><th>Start</th><th>End</th><th>Type</th></tr></thead><tbody>\n");
        foreach (var f in report.Findings)
        {
            b.Append("<tr><td>").Append(E(f.Id)).Append("</td><td><code>").Append(E(f.Principal.Id)).Append("</code></td><td><code>").Append(E(f.Evidence.AssignmentId)).Append("</code></td><td>").Append(E(Date(f.Evidence.StartDateTime))).Append("</td><td>").Append(E(Date(f.Evidence.EndDateTime))).Append("</td><td>").Append(E(f.Evidence.AssignmentType?.ToString() ?? f.Evidence.MemberType ?? "—")).Append("</td></tr>\n");
        }

        b.Append("</tbody></table></div></details></section>\n</main>\n<footer class=\"wrap\">Generated by <a href=\"https://elevate.reothor.no/audit.html\">elevate-audit</a>, the standing-access companion to Elevate. Sample data is fictional.</footer>\n");
        b.Append("<script>addEventListener('beforeprint',()=>document.querySelectorAll('details').forEach(d=>d.open=true));</script>\n</body>\n</html>\n");
        return b.ToString();
    }

    private static void Meta(StringBuilder b, string label, string value) =>
        b.Append("<li>").Append(E(label)).Append("<strong>").Append(E(value)).Append("</strong></li>\n");

    private static void Card(StringBuilder b, string severity, int count) =>
        b.Append("<div class=\"card ").Append(severity).Append("\"><div class=\"count\">").Append(count.ToString(CultureInfo.InvariantCulture)).Append("</div><div class=\"label\">").Append(severity).Append("</div></div>\n");

    private static string Humanize(ScopeKind kind) => kind switch
    {
        ScopeKind.AdministrativeUnit => "administrative unit",
        ScopeKind.ManagementGroup => "management group",
        ScopeKind.ResourceGroup => "resource group",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Date(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—";

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
```

The footer's "Sample data is fictional." sentence is unconditional on purpose: the committed sample is the only report the public sees, and a real tenant's admin knows their own data. If that reads wrong for real reports, make it conditional on `report.Tool.Version == "sample"` — but then keep the golden updated.

- [ ] **Step 5: Generate the golden, run everything, validate the site**

```bash
ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln
dotnet test audit/Elevate.Audit.sln
python3 scripts/check-site.py
```

Expected: `site/audit-sample.html` written; all tests pass; the site validator passes (one h1, no images, https links only). Open `site/audit-sample.html` in a browser once (`python3 -m http.server 4173 --directory site`) and check it reads like the product page: same blue, same greys, rounded cards.

- [ ] **Step 6: Commit**

```bash
git add audit site/audit-sample.html
git commit -m "audit: HTML report with the product page's tokens, plus the committed sample"
```

---

### Task 17: The `scan` command, `update`, and the end-to-end path

**Files:**
- Create: `audit/src/Elevate.Audit/Commands/ScanCommand.cs`, `audit/src/Elevate.Audit/Commands/UpdateCommand.cs`, `audit/src/Elevate.Audit/Update/ReleaseChecker.cs`, `audit/src/Elevate.Audit/Networking/LoggingHttpClient.cs`
- Modify: `audit/src/Elevate.Audit/Program.cs`
- Test: `audit/tests/Elevate.Audit.Tests/ScanCommandTests.cs`, `audit/tests/Elevate.Audit.Tests/ReleaseCheckerTests.cs`

**Interfaces:**
- Produces: `ScanCommand.AddTo(RootCommand)`; options `--tenant`, `--client-id`, `--device-code`, `--skip-azure`, `--all-roles`, `--min-severity`, `--ignore`, `--no-fail`, `--json`, `--html`, `--save-snapshot`, `--from-snapshot`, `--quiet`/`-q`, `--no-color`, `--verbose`; `ReleaseChecker(IHttpClient http, Uri? url = null)` with `Task<Release?> LatestAsync(CancellationToken)` and `static bool IsNewer(string tag, string current)`.

- [ ] **Step 1: Write the failing tests**

`audit/tests/Elevate.Audit.Tests/ScanCommandTests.cs`:

```csharp
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
        doc.RootElement.GetProperty("summary").GetProperty("high").GetInt32().Should().Be(12);
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
    public async Task HtmlAndJsonFiles_AreWritten_AndTerminalStillPrints()
    {
        var dir = Directory.CreateTempSubdirectory("elevate-audit").FullName;
        var html = Path.Combine(dir, "report.html");
        var json = Path.Combine(dir, "report.json");

        var (code, stdout, _) = await RunAsync("--from-snapshot", SnapshotPath(), "--html", html, "--json", json, "--no-color");

        code.Should().Be(2);
        File.ReadAllText(html).Should().StartWith("<!doctype html>");
        File.ReadAllText(json).Should().Contain("\"findings\"");
        stdout.Should().Contain("ENTRA-USER-PERMANENT").And.Contain("12 high");
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
```

Update `CommandTreeTests` to join the same collection (`[Collection("console")]`) since it redirects `Console.Out`, and assert the root now has `version` and `update`.

`audit/tests/Elevate.Audit.Tests/ReleaseCheckerTests.cs`:

```csharp
using Elevate.Audit.Tests.Support;
using Elevate.Audit.Update;
using FluentAssertions;

namespace Elevate.Audit.Tests;

public class ReleaseCheckerTests
{
    [Fact]
    public async Task Latest_PicksTheNewestReleaseWithAnAuditAsset()
    {
        var stub = new StubHttpClient();
        stub.On("GET", "/releases", """
            [
              {"tag_name":"v2.0.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v2.0.0","draft":false,"prerelease":false,"assets":[{"name":"elevate-cli-2.0.0-linux-x64.tar.gz"}]},
              {"tag_name":"v1.9.0","html_url":"https://github.com/FrodeHus/elevate/releases/tag/v1.9.0","draft":false,"prerelease":false,"assets":[{"name":"elevate-audit-1.9.0-linux-x64.tar.gz"}]}
            ]
            """);

        var latest = await new ReleaseChecker(stub).LatestAsync(CancellationToken.None);

        latest!.Version.Should().Be("1.9.0");
        stub.Requests[0].Headers["User-Agent"].Should().Be("elevate-audit");
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.2", true)]
    [InlineData("v1.2.3", "1.2.3", false)]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("v1.2.3", "0.0.0", true)]
    [InlineData("v1.2.3", "1.2.3+abc", false)]
    public void IsNewer_ComparesNumerically(string tag, string current, bool expected) =>
        ReleaseChecker.IsNewer(tag, current).Should().Be(expected);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: build errors.

- [ ] **Step 3: Implement**

`audit/src/Elevate.Audit/Networking/LoggingHttpClient.cs`:

```csharp
using Elevate.Core.Networking;

namespace Elevate.Audit.Networking;

/// <summary>`--verbose`: one line per request on stderr, after the reply.</summary>
public sealed class LoggingHttpClient(IHttpClient inner, Action<string> log) : IHttpClient
{
    public async Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        var response = await inner.SendAsync(request, ct).ConfigureAwait(false);
        log($"{request.Method} {request.Url} → {response.Status}");
        return response;
    }
}
```

`audit/src/Elevate.Audit/Update/ReleaseChecker.cs`:

```csharp
using System.Text.Json;
using Elevate.Core.Models;
using Elevate.Core.Networking;

namespace Elevate.Audit.Update;

/// <summary>The newest published release that ships an elevate-audit archive. Same shape as the CLI's checker, different asset prefix.</summary>
public sealed class ReleaseChecker(IHttpClient http, Uri? url = null)
{
    public const string AssetPrefix = "elevate-audit-";

    public static readonly Uri ReleasesUrl = new("https://api.github.com/repos/FrodeHus/elevate/releases?per_page=20");

    public sealed record Release(string Tag, Uri Url)
    {
        public string Version => Tag.StartsWith('v') || Tag.StartsWith('V') ? Tag[1..] : Tag;
    }

    private sealed record Asset(string? Name);

    private sealed record Wire(string? Tag_name, Uri? Html_url, bool? Draft, bool? Prerelease, List<Asset>? Assets);

    private readonly Uri _url = url ?? ReleasesUrl;

    public async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        var request = new HttpRequestData("GET", _url, new Dictionary<string, string>
        {
            ["Accept"] = "application/vnd.github+json",
            ["User-Agent"] = "elevate-audit",
        }, null);
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.Status == 404)
        {
            return null;
        }

        if (response.Status is < 200 or >= 300)
        {
            throw new PimException(PimErrorKind.Unexpected, response.BodyText, response.Status);
        }

        List<Wire>? releases;
        try
        {
            releases = JsonSerializer.Deserialize<List<Wire>>(response.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            throw new PimException(PimErrorKind.Unexpected, "Could not read the release information from GitHub", response.Status);
        }

        var release = releases?.FirstOrDefault(r =>
            r.Tag_name is { } tag && tag.StartsWith('v')
            && r.Html_url is not null && r.Draft != true && r.Prerelease != true
            && r.Assets?.Any(a => a.Name?.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) == true) == true);
        return release is null ? null : new Release(release.Tag_name!, release.Html_url!);
    }

    /// <summary>Numeric comparison of the dotted prefix; build metadata and pre-release suffixes are ignored.</summary>
    public static bool IsNewer(string tag, string current)
    {
        static Version Parse(string text)
        {
            var core = text.TrimStart('v', 'V');
            var cut = core.IndexOfAny(['-', '+']);
            if (cut >= 0)
            {
                core = core[..cut];
            }

            var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
            while (parts.Count < 3)
            {
                parts.Add(0);
            }

            return new Version(parts[0], parts[1], parts[2]);
        }

        return Parse(tag) > Parse(current);
    }
}
```

`audit/src/Elevate.Audit/Commands/UpdateCommand.cs`:

```csharp
using System.CommandLine;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Update;
using Elevate.Core.Networking;

namespace Elevate.Audit.Commands;

public static class UpdateCommand
{
    public static Command Create()
    {
        var command = new Command("update", "Check GitHub for a newer release of elevate-audit.");
        command.SetAction(async (_, ct) =>
        {
            var checker = new ReleaseChecker(new HttpClientAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
            var latest = await checker.LatestAsync(ct).ConfigureAwait(false);
            var current = AppInfo.Version;
            if (latest is null)
            {
                Console.Out.WriteLine($"{AppInfo.Name} {current}; no release with an elevate-audit build was found.");
            }
            else if (ReleaseChecker.IsNewer(latest.Tag, current))
            {
                Console.Out.WriteLine($"{AppInfo.Name} {current}; {latest.Version} is available: {latest.Url}");
            }
            else
            {
                Console.Out.WriteLine($"{AppInfo.Name} {current} is up to date.");
            }

            return ExitCodes.Ok;
        });
        return command;
    }
}
```

`audit/src/Elevate.Audit/Commands/ScanCommand.cs`:

```csharp
using System.CommandLine;
using Elevate.Audit.Auth;
using Elevate.Audit.Collectors;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;
using Elevate.Audit.Networking;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;

namespace Elevate.Audit.Commands;

/// <summary>The default command: sign in (or load a snapshot), scan, run the rules, render, set the exit code.</summary>
public static class ScanCommand
{
    public static readonly Option<string> TenantOption = new("--tenant") { Description = "Tenant id or domain to scan. Default: the account's home tenant.", DefaultValueFactory = _ => "organizations" };
    public static readonly Option<string?> ClientIdOption = new("--client-id") { Description = $"Public client to use for Microsoft Graph instead of {ClientIds.GraphDefaultDisplayName}; it must be consented to the same read scopes." };
    public static readonly Option<bool> DeviceCodeOption = new("--device-code") { Description = "Sign in with a device code instead of the browser; for SSH sessions and containers." };
    public static readonly Option<bool> SkipAzureOption = new("--skip-azure") { Description = "Do not sign in to Azure Resource Manager or scan Azure RBAC." };
    public static readonly Option<bool> AllRolesOption = new("--all-roles") { Description = "Report every role, not only the privileged ones." };
    public static readonly Option<string> MinSeverityOption = new("--min-severity") { Description = "Lowest severity to show: high, medium, low or info.", DefaultValueFactory = _ => "info" };
    public static readonly Option<string[]> IgnoreOption = new("--ignore") { Description = "Rule code to skip (repeatable), e.g. --ignore GA-COUNT.", AllowMultipleArgumentsPerToken = true };
    public static readonly Option<bool> NoFailOption = new("--no-fail") { Description = "Exit 0 even when there are high findings." };
    public static readonly Option<string?> JsonOption = new("--json") { Description = "Write the JSON report to this file, or - for stdout (then nothing else goes to stdout)." };
    public static readonly Option<string?> HtmlOption = new("--html") { Description = "Write a self-contained HTML report to this file." };
    public static readonly Option<string?> SaveSnapshotOption = new("--save-snapshot") { Description = "Also write everything that was read to this file, for offline re-rendering or a bug report." };
    public static readonly Option<string?> FromSnapshotOption = new("--from-snapshot") { Description = "Skip sign-in and reading; run the rules on a saved snapshot." };
    public static readonly Option<bool> QuietOption = new("--quiet", "-q") { Description = "Only the summary line on stdout and no progress on stderr." };
    public static readonly Option<bool> NoColorOption = new("--no-color") { Description = "Plain output (NO_COLOR is honoured too)." };
    public static readonly Option<bool> VerboseOption = new("--verbose") { Description = "Log every request on stderr." };

    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var option in new Option[] { TenantOption, ClientIdOption, DeviceCodeOption, SkipAzureOption, AllRolesOption, MinSeverityOption, IgnoreOption, NoFailOption, JsonOption, HtmlOption, SaveSnapshotOption, FromSnapshotOption, QuietOption, NoColorOption, VerboseOption })
        {
            root.Options.Add(option);
        }

        root.SetAction(RunAsync);
    }

    public static async Task<int> RunAsync(ParseResult parse, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parse);
        var terminal = new Terminal(parse.GetValue(QuietOption), parse.GetValue(NoColorOption));
        if (!Severities.TryParse(parse.GetValue(MinSeverityOption), out var minSeverity))
        {
            throw new AuditException($"Unknown severity '{parse.GetValue(MinSeverityOption)}'. Use high, medium, low or info.");
        }

        var options = new AuditOptions(
            AllRoles: parse.GetValue(AllRolesOption),
            Ignored: (parse.GetValue(IgnoreOption) ?? []).Select(i => i.ToUpperInvariant()).ToList(),
            MinSeverity: minSeverity,
            SkipAzure: parse.GetValue(SkipAzureOption));
        var jsonTarget = parse.GetValue(JsonOption);
        var jsonToStdout = jsonTarget == "-";

        Snapshot snapshot;
        IReadOnlyList<string> scopesRequested;
        if (parse.GetValue(FromSnapshotOption) is { } snapshotPath)
        {
            snapshot = SnapshotFile.Load(snapshotPath);
            scopesRequested = [];
            terminal.Note($"Loaded snapshot {snapshotPath} (scanned {snapshot.ScannedAt:u}).");
        }
        else
        {
            var clientId = parse.GetValue(ClientIdOption);
            scopesRequested = clientId is null ? ClientIds.GraphReadScopeNames : [".default"];
            snapshot = await ScanLiveAsync(parse.GetValue(TenantOption)!, clientId, parse.GetValue(DeviceCodeOption), parse.GetValue(VerboseOption), options, terminal, ct).ConfigureAwait(false);
        }

        if (parse.GetValue(SaveSnapshotOption) is { } savePath)
        {
            SnapshotFile.Save(snapshot, savePath);
            terminal.Note($"Snapshot written to {savePath}.");
        }

        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, RuleRunner.Visible(findings, options), options, AppInfo.Version, scopesRequested);

        if (jsonTarget is not null)
        {
            var json = JsonRenderer.Render(report);
            if (jsonToStdout)
            {
                Console.Out.Write(json);
            }
            else
            {
                File.WriteAllText(jsonTarget, json);
                terminal.Note($"JSON report written to {jsonTarget}.");
            }
        }

        if (parse.GetValue(HtmlOption) is { } htmlPath)
        {
            File.WriteAllText(htmlPath, HtmlRenderer.Render(report));
            terminal.Note($"HTML report written to {htmlPath}.");
        }

        if (!jsonToStdout)
        {
            TerminalRenderer.Render(report, terminal.Stdout, summaryOnly: terminal.Quiet);
        }

        return RuleRunner.HasHigh(findings) && !parse.GetValue(NoFailOption) ? ExitCodes.HighFindings : ExitCodes.Ok;
    }

    private static async Task<Snapshot> ScanLiveAsync(string tenant, string? clientId, bool deviceCode, bool verbose, AuditOptions options, Terminal terminal, CancellationToken ct)
    {
        var provider = new AuditTokenProvider(clientId, tenant, deviceCode, terminal.Say);
        Identity identity;
        try
        {
            identity = await provider.SignInAsync(ct).ConfigureAwait(false);
        }
        catch (PimException e)
        {
            throw new AuditException(MsalErrors.Explain(e, provider.GraphClientId));
        }

        IHttpClient http = new RetryingHttpClient(new HttpClientAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(100) }));
        if (verbose)
        {
            http = new LoggingHttpClient(http, line => terminal.Stderr.MarkupLine($"[grey]{Spectre.Console.Markup.Escape(line)}[/]"));
        }

        var graph = new GraphTransport(http, provider);
        var arm = options.SkipAzure ? null : new GraphTransport(http, provider, GraphTransport.MapArmError);
        var tenantId = Guid.TryParse(tenant, out _) ? tenant : identity.HomeTenantId;
        terminal.Note($"Signed in as {identity.Upn}.");
        try
        {
            return await new Scanner(graph, arm, identity, tenantId, options, AppInfo.Version, terminal.Note).ScanAsync(ct).ConfigureAwait(false);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.Forbidden or PimErrorKind.ConsentRequired)
        {
            throw new AuditException(MsalErrors.Explain(e, provider.GraphClientId));
        }
    }
}
```

In `Program.BuildRootCommand()` add `ScanCommand.AddTo(root);` and `root.Subcommands.Add(UpdateCommand.Create());`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test audit/Elevate.Audit.sln`
Expected: all pass. If System.CommandLine reports a parse error as exit code 1 with its own message for `--min-severity urgent` before our code runs, that is fine as long as the test's `Contain("Unknown severity")` holds; if it does not, validate inside `RunAsync` as written (the option is a plain string, so parsing succeeds and our check runs).

- [ ] **Step 5: Smoke the published binary end to end against a snapshot**

```bash
./audit/package.sh publish 0.0.0 osx-arm64
./audit/dist/osx-arm64/elevate-audit --from-snapshot audit/tests/Elevate.Audit.Tests/Fixtures/snapshots/sample.json --html /tmp/sample.html --no-color; echo "exit $?"
```

Expected: the terminal report, `exit 2`, and `/tmp/sample.html` identical to `site/audit-sample.html` except the tool version line.

- [ ] **Step 6: Commit**

```bash
git add audit
git commit -m "audit: scan command with outputs and exit codes, update command"
```

---

### Task 18: Documentation

**Files:**
- Create: `audit/README.md`, `docs/audit.md`
- Modify: `docs/README.md` (one bullet under Reference), `docs/entra-app-registration.md` (new section 7 at the end), `docs/releasing.md` (one paragraph)

**Interfaces:** none. The site validator in Task 19 checks that `docs/audit.md` exists and that the headings linked from `site/audit.html` (`#2-what-it-asks-for`, `#5-a-tenant-that-blocks-the-graph-powershell-app`) exist, so keep the headings below verbatim.

- [ ] **Step 1: `docs/audit.md`**

````markdown
# Finding standing access with elevate-audit

`elevate-audit` is a companion command-line tool to Elevate. It signs in as an administrator,
reads your tenant, and lists every piece of *standing* privileged access that could become
PIM eligibility instead: permanent Entra role assignments held by people or by groups (with the
nested groups resolved), permanent members of groups that PIM for Groups already manages, and
permanent Owner, Contributor and similar assignments in Azure. It writes nothing. Nothing leaves
your machine.

It is separate from the Elevate app and CLI: a different binary, no shared settings, no shared
sign-in, and it never touches the Elevate app registration.

Related: [Getting started](getting-started.md), [Setting up the Entra app registration](entra-app-registration.md).

## 1. Install

macOS and Linux with Homebrew:

```sh
brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew trust frodehus/elevate
brew install frodehus/elevate/elevate-audit
```

Windows: `winget install Reothor.Elevate.Audit` once the manifest is published; until then, or on
any platform, download `elevate-audit-<version>-<platform>.tar.gz` (`.zip` on Windows) from the
[latest release](https://github.com/FrodeHus/elevate/releases/latest), unpack it anywhere on your
PATH, and verify it with `sha256sum -c elevate-audit-<version>-checksums.txt`.

## 2. What it asks for

`elevate-audit` needs no app registration. It signs in with two Microsoft public clients:

| Resource | Client | What it asks for |
|---|---|---|
| Microsoft Graph | **Microsoft Graph Command Line Tools** (`14d82eec-204b-4c2f-b7e8-296a70dab67e`), Microsoft's own multi-tenant public client | Six delegated, read-only scopes, listed below |
| Azure Resource Manager | **Azure CLI** (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) | `.default`, which is what the Azure CLI itself uses |

The Graph scopes and why each one is needed:

| Scope | Used for |
|---|---|
| `User.Read` | The tenant's display name and the signed-in account for the report header. |
| `RoleManagement.Read.Directory` | Entra role definitions, every active role assignment and every eligibility. |
| `PrivilegedAssignmentSchedule.Read.AzureADGroup` | Active memberships and ownerships of groups managed by PIM for Groups. |
| `PrivilegedEligibilitySchedule.Read.AzureADGroup` | Eligible memberships and ownerships of those groups. |
| `GroupMember.Read.All` | Expanding the members of role-assigned groups, including nested groups. |
| `User.ReadBasic.All` | Names and sign-in names of the people found, instead of bare object ids. |

All of them are read scopes; several require an administrator to consent. Since the person
running a standing-access audit is a privileged administrator, you consent for yourself at the
first sign-in. The consent screen is Microsoft's, and names Microsoft's tool, not Elevate.

Tokens live in memory for one run and are never written to disk. There is nothing to sign out of.

## 3. Who can run it

The signed-in account needs a directory role that can list PIM assignments tenant-wide: **Global
Reader**, **Privileged Role Administrator** or **Security Reader** all work. For the Azure part,
**Reader** on the management groups or subscriptions you want covered. Without Azure access the
Azure section is skipped and the report says so.

## 4. Run it

```sh
elevate-audit
```

Sign in when the browser opens (or pass `--device-code` over SSH), accept the consent prompt
once, and read the report in the terminal. Useful options:

| Option | Effect |
|---|---|
| `--html report.html` | Also write a self-contained HTML report you can share or print. |
| `--json report.json` (or `--json -`) | Also write the JSON report, or print only JSON on stdout. |
| `--all-roles` | Report every role, not only the privileged ones. |
| `--min-severity medium` | Hide low and informational findings. |
| `--ignore GA-COUNT` | Skip a rule (repeatable). |
| `--skip-azure` | Do not sign in to Azure or scan Azure RBAC. |
| `--tenant <id>` | Scan a tenant you are a guest in. |
| `--save-snapshot scan.json` / `--from-snapshot scan.json` | Save everything that was read; re-run the rules offline later, or attach the snapshot to a bug report. |
| `--client-id <guid>` | Use your own public client for Graph; see section 5. |

Exit codes: `0` no high findings, `2` at least one high finding, `1` error. `--no-fail` forces
`0` so a pipeline can publish the report without failing on it.

### The rules

| Code | Finding | Severity |
|---|---|---|
| `ENTRA-USER-PERMANENT` | A user holds a permanent active Entra role directly. | High |
| `ENTRA-GROUP-PERMANENT` | A group holds a permanent active Entra role; one finding for the group and one per person in it, with the path through nested groups. | High |
| `ENTRA-GROUP-NOT-PIM` | A role-assignable group carrying a role is not onboarded to PIM for Groups. | Medium |
| `ENTRA-GROUP-NOT-ASSIGNABLE` | A role is assigned to a group that is not role-assignable, which PIM for Groups cannot govern. | Medium |
| `GROUP-MEMBER-PERMANENT` | A group managed by PIM for Groups still has a permanent member or owner. | High |
| `AZURE-PERMANENT` | A permanent privileged Azure role assignment at any scope. | High for Owner, User Access Administrator and RBAC Administrator; Medium otherwise |
| `SP-PERMANENT` | A service principal or managed identity holds a permanent privileged role. PIM does not apply; review the workload identity instead. | Info |
| `GUEST-PERMANENT` | A guest holds a permanent privileged role, directly or through a group. | High |
| `ELIGIBLE-NO-END` | An eligibility has no end date. | Low |
| `GA-COUNT` | Fewer than 2 or more than 5 people can become Global Administrator. | Medium |

"Privileged" means the roles Microsoft Graph marks `isPrivileged` for Entra, and for Azure:
Owner, Contributor, User Access Administrator, Role Based Access Control Administrator, Key
Vault Administrator, Key Vault Data Access Administrator, Storage Account Contributor, Virtual
Machine Administrator Login, Azure Kubernetes Service RBAC Cluster Admin, Security Admin, and any
custom role whose actions include `*` or can write role assignments.

A currently *activated* PIM assignment is not standing access and is never reported.

## 5. A tenant that blocks the Graph PowerShell app

Some tenants block Microsoft Graph Command Line Tools with an app consent policy or Conditional
Access. `elevate-audit` then reports the failing scope or the AADSTS code and exits. Pass any
public client your tenant allows with `--client-id`:

1. Register a public client (or reuse one you own, for example your own Elevate registration —
   see [section 7 of the app registration guide](entra-app-registration.md#7-optional-read-scopes-for-elevate-audit)).
2. Under **Authentication**, add the platform **Mobile and desktop applications** with the
   redirect URI `http://localhost`.
3. Under **API permissions**, add the six delegated Microsoft Graph scopes from section 2 and
   grant admin consent.
4. Run `elevate-audit --client-id <application id>`.

With a custom client the tool asks for `https://graph.microsoft.com/.default`, so it can only do
what that registration was consented for.

## 6. Reading the report

The HTML report opens with the counts per severity and a **Start here** list of the high
findings, each with a one-line remedy and a link to the right portal blade. Then one section per
rule; group findings fold the membership path under "through N groups". The appendix lists the
assignment ids for auditors and the scopes the tool requested.

Remedies are the standard PIM moves: convert a permanent assignment to eligible, onboard a group
to PIM for Groups, replace a dynamic or non-role-assignable group with a static role-assignable
one, or, for service principals, review the workload identity's need for the role.

A sample report from a fictional tenant is at
<https://elevate.reothor.no/audit-sample.html>.

## 7. Troubleshooting

- **"Consent … was declined or is not permitted"** — accept the six read scopes, or use
  `--client-id` (section 5).
- **"The signed-in account may not list the tenant's role assignments"** — you need Global
  Reader, Privileged Role Administrator or Security Reader (section 3).
- **The Azure section says "skipped"** — the account sees no subscriptions, or the ARM sign-in
  was cancelled. Grant Reader, or pass `--skip-azure` to silence it.
- **A group shows as "not onboarded" although it is** — the account cannot read that group's
  PIM data; for role-assignable groups that needs Global Reader or Privileged Role Administrator
  at directory scope.
- **Throttled** — the tool retries `429` replies honouring `Retry-After`; `--verbose` shows each
  request.
````

- [ ] **Step 2: `audit/README.md`**

```markdown
# Elevate Audit

`elevate-audit` finds standing privileged access in a Microsoft Entra tenant — permanent role
assignments held by people and groups, permanent members of PIM-managed groups, permanent Azure
Owner and Contributor assignments — and lists what should become PIM eligibility instead. It is a
separate, read-only companion to the Elevate app and CLI: its own binary, no shared settings, no
app registration needed.

User guide: [docs/audit.md](../docs/audit.md). Design: [the spec](../docs/superpowers/specs/2026-09-13-elevate-audit-design.md).

```text
$ elevate-audit --html report.html
╭─ elevate-audit ─────────────────────────────────────────────────────────────╮
│ Tenant   Contoso · 72f988bf-0000-4000-8000-2d7cd011db47                     │
│ Account  alex.rivera@contoso.com                                            │
╰─────────────────────────────────────────────────────────────────────────────╯
╭─ ENTRA-GROUP-PERMANENT · high · 3 ──────────────────────────────────────────╮
│ Principal            Role                  Scope      Via                   │
│ Tier 0 Admins (group) Global Administrator Directory  direct                │
│ Sam Chen             Global Administrator  Directory  Tier 0 Admins         │
│ Casey Wong           Global Administrator  Directory  Tier 0 Admins ← Platform Team │
╰─────────────────────────────────────────────────────────────────────────────╯
24 findings: 12 high, 5 medium, 5 low, 2 info.
```

## Build and test

```sh
dotnet test audit/Elevate.Audit.sln
./audit/package.sh publish 0.0.0 osx-arm64      # one self-contained binary in audit/dist/osx-arm64/
./audit/dist/osx-arm64/elevate-audit --from-snapshot audit/tests/Elevate.Audit.Tests/Fixtures/snapshots/sample.json
```

Golden files (`audit/tests/Elevate.Audit.Tests/Golden/`, `Fixtures/snapshots/sample.json`,
`site/audit-sample.html`) are regenerated with `ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln`.

## Layout

`src/Elevate.Audit` — `Auth` (two MSAL public clients, in memory), `Collectors` (Graph and ARM
reads into an immutable `Snapshot`), `Rules` (pure `Snapshot → Finding` rules), `Rendering`
(terminal, JSON, HTML), `Commands`. `tests/Elevate.Audit.Tests` — stub-HTTP collector tests,
builder-based rule tests, golden renderer tests. References `windows/src/Elevate.Core` for the
Graph transport, JSON options and the Entra role catalogue.
```

- [ ] **Step 3: Edits to existing docs**

`docs/README.md`, under "Reference:", after the shared app registration bullet:

```markdown
- [Finding standing access with elevate-audit](audit.md) — the read-only companion tool that
  lists permanent privileged assignments, nested group members and PIM-managed groups with
  standing members, so they can be moved to PIM.
```

`docs/entra-app-registration.md`, appended at the end:

```markdown
## 7. Optional: read scopes for elevate-audit

[elevate-audit](audit.md) does not use this registration; by default it signs in with
Microsoft's own Graph PowerShell client. If your tenant blocks that client, you can point
`elevate-audit --client-id` at this registration instead. Add these delegated Microsoft Graph
permissions and grant admin consent; Elevate itself never asks for them:

`User.Read` (already present), `RoleManagement.Read.Directory`,
`PrivilegedAssignmentSchedule.Read.AzureADGroup`, `PrivilegedEligibilitySchedule.Read.AzureADGroup`,
`GroupMember.Read.All`, `User.ReadBasic.All`.

With a custom client the auditor requests `https://graph.microsoft.com/.default`, so it can do
only what was consented.
```

`docs/releasing.md`, after the paragraph describing the `cli` job (search for `**cli** (a matrix`), add:

```markdown
**audit** mirrors **cli** for the `elevate-audit` companion: the same three-leg matrix, tests from
`audit/Elevate.Audit.sln`, `audit/package.sh publish|archive`, the same signing steps, archives
named `elevate-audit-<version>-<rid>`, a `Reothor.Elevate.Audit` winget manifest as the
`winget-audit-manifest` artifact. `publish` adds the archives and an
`elevate-audit-<version>-checksums.txt` to the release and regenerates
`Formula/elevate-audit.rb` with `scripts/update-audit-formula.sh` in the same commit as the cask.
```

- [ ] **Step 4: Verify links**

Run: `python3 scripts/check-site.py`
Expected: still passes (the site does not link the new docs yet). Read `docs/audit.md` once top to bottom for a stray real name or address.

- [ ] **Step 5: Commit**

```bash
git add audit/README.md docs/audit.md docs/README.md docs/entra-app-registration.md docs/releasing.md
git commit -m "docs: elevate-audit user guide and cross-references"
```

---

### Task 19: The product page for the auditor

**Files:**
- Create: `site/audit.html`
- Modify: `site/index.html` (navigation link and footer link)

**Interfaces:** none. Constraints from `scripts/check-site.py`: exactly one `<h1>`, relative links must resolve to files in `site/`, `github.com/frodehus/elevate/blob/main/...` links must resolve to files and headings in this checkout, every other link must be `https://`, images need `alt`, `width` and `height`.

- [ ] **Step 1: Write `site/audit.html`**

The header and footer are copied from `site/consent.html` and `site/index.html` respectively (adjusting `href="#"` to `index.html`). The page uses the shared stylesheet and a small scoped `<style>` for the parts the product page has no class for.

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="theme-color" content="#f5f7fb" />
    <meta
      name="description"
      content="elevate-audit finds permanent privileged role assignments, nested group members and PIM-managed groups with standing members, so you can move them to PIM. Read-only, no app registration."
    />
    <meta property="og:type" content="website" />
    <meta property="og:title" content="Elevate Audit — find the access that should be PIM" />
    <meta property="og:description" content="A read-only companion to Elevate. Sign in once, get a list of standing privileged access, nested groups resolved." />
    <meta property="og:url" content="https://elevate.reothor.no/audit.html" />
    <meta property="og:image" content="https://elevate.reothor.no/assets/social-preview.png" />
    <link rel="canonical" href="https://elevate.reothor.no/audit.html" />
    <link rel="icon" type="image/png" href="assets/icon.png" />
    <link rel="stylesheet" href="styles.css" />
    <title>Elevate Audit — standing access, found</title>
    <style>
      .audit-main { padding-block: 64px 96px; }
      .audit-main h1 { font-size: clamp(40px, 6vw, 72px); margin-bottom: 16px; }
      .audit-lede { color: var(--muted); font-size: 20px; max-width: 46rem; }
      .audit-main h2 { font-size: 28px; margin: 64px 0 16px; }
      .audit-main p, .audit-main li { max-width: 46rem; line-height: 1.6; }
      .audit-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 16px; margin-top: 24px; }
      .audit-card { border: 1px solid var(--line); border-radius: 20px; padding: 20px 22px; background: var(--mist); }
      .audit-card h3 { font-size: 17px; margin-bottom: 6px; }
      .audit-card p { color: var(--muted); font-size: 15px; margin: 0; }
      .audit-table { overflow-x: auto; margin-top: 16px; }
      .audit-table table { width: 100%; border-collapse: collapse; font-size: 15px; }
      .audit-table th, .audit-table td { text-align: left; vertical-align: top; padding: 10px 12px; border-top: 1px solid var(--line); }
      .audit-table th { color: var(--muted); font-weight: 600; border-top: 0; }
      .audit-main pre { background: var(--navy); color: #e8eef7; border-radius: 16px; padding: 20px 24px; overflow-x: auto; font-size: 13.5px; line-height: 1.5; }
      .audit-main code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
      .audit-main p code, .audit-main li code, .audit-table code { background: var(--mist); border-radius: 6px; padding: 1px 6px; font-size: 0.92em; }
      .audit-actions { display: flex; flex-wrap: wrap; gap: 16px; align-items: center; margin-top: 28px; }
      .audit-note { border-left: 3px solid var(--blue); padding-left: 16px; color: var(--muted); }
    </style>
  </head>
  <body>
    <a class="skip-link" href="#main">Skip to content</a>
    <header class="site-header">
      <nav class="nav-glass" aria-label="Main navigation">
        <a class="brand" href="index.html"
          ><img src="assets/icon.png" width="32" height="32" alt="" /><span>Elevate</span></a
        >
        <div class="nav-links">
          <a href="#what">What it finds</a><a href="#permissions">Permissions</a><a href="#install">Install</a><a href="audit-sample.html">Sample report</a>
        </div>
        <a class="button button-small" href="#install">Get elevate-audit <span aria-hidden="true">↓</span></a>
      </nav>
    </header>

    <main id="main" class="audit-main wrap">
      <p class="hero-intro">A companion to Elevate, for the administrator.</p>
      <h1>Find the access that should have been PIM.</h1>
      <p class="audit-lede">
        <code>elevate-audit</code> signs in as you, reads your tenant, and lists every permanent
        privileged assignment that could be eligibility instead: people, groups, the people inside
        nested groups, PIM-managed groups with standing members, and Azure Owners and Contributors.
        Read-only. No app registration. Nothing leaves your machine.
      </p>
      <div class="audit-actions">
        <a class="button" href="#install">Install <span aria-hidden="true">↓</span></a>
        <a class="text-link" href="audit-sample.html">See a sample report <span aria-hidden="true">→</span></a>
        <a class="text-link" href="https://github.com/FrodeHus/elevate/blob/main/docs/audit.md">Read the guide <span aria-hidden="true">↗</span></a>
      </div>

      <h2 id="what">What it finds</h2>
      <div class="audit-grid">
        <div class="audit-card"><h3>Permanent Entra roles</h3><p>Users with a permanent active privileged role, instead of an eligible one.</p></div>
        <div class="audit-card"><h3>Groups, unrolled</h3><p>Role-assigned groups and every person inside them, through nested groups, with the path that gets them there.</p></div>
        <div class="audit-card"><h3>PIM for Groups leftovers</h3><p>Groups already managed by PIM that still have permanent members or owners.</p></div>
        <div class="audit-card"><h3>Azure RBAC</h3><p>Permanent Owner, Contributor and User Access Administrator assignments at every scope, group principals expanded.</p></div>
        <div class="audit-card"><h3>Guests and workload identities</h3><p>Guests holding privileged roles, and service principals that PIM cannot help with, listed separately.</p></div>
        <div class="audit-card"><h3>Hygiene</h3><p>Eligibilities without an end date, groups not onboarded to PIM, and a Global Administrator count outside two to five.</p></div>
      </div>
      <p class="audit-note" style="margin-top: 24px;">
        A currently activated PIM assignment is not standing access and never appears. By default only
        privileged roles are reported; <code>--all-roles</code> widens it.
      </p>

      <h2 id="how">How it works</h2>
      <pre><code>$ elevate-audit --html report.html
Opening the browser to sign in to Microsoft Graph…
Reading directory roles… Expanding groups… Reading Azure role assignments…
╭─ ENTRA-GROUP-PERMANENT · high · 3 ─────────────────────────────────────────────╮
│ Principal              Role                  Scope      Via                     │
│ Tier 0 Admins (group)  Global Administrator  Directory  direct                  │
│ Sam Chen               Global Administrator  Directory  Tier 0 Admins           │
│ Casey Wong             Global Administrator  Directory  Tier 0 Admins ← Platform Team │
╰────────────────────────────────────────────────────────────────────────────────╯
24 findings: 12 high, 5 medium, 5 low, 2 info.
HTML report written to report.html.</code></pre>
      <p>
        The terminal shows the findings grouped by rule. <code>--html</code> writes a self-contained report
        styled like this page, for the team that owns the roles or for a change ticket; <code>--json</code>
        gives scripts the same data. The exit code is <code>2</code> when there are high findings, so a
        pipeline can track the number going down.
      </p>

      <h2 id="permissions">Permissions, and why there is no app to register</h2>
      <p>
        The tool signs in with two public clients Microsoft already publishes:
        <strong>Microsoft Graph Command Line Tools</strong> for Microsoft Graph, and the
        <strong>Azure CLI</strong> client for Azure Resource Manager, the same way Elevate's Azure CLI
        sign-in method works. Consent is dynamic: at the first sign-in you, as the administrator running
        the audit, approve exactly these read-only Microsoft Graph scopes.
      </p>
      <div class="audit-table">
        <table>
          <thead><tr><th>Scope</th><th>Used for</th></tr></thead>
          <tbody>
            <tr><td><code>User.Read</code></td><td>The tenant name and your account, for the report header.</td></tr>
            <tr><td><code>RoleManagement.Read.Directory</code></td><td>Role definitions, active assignments and eligibilities.</td></tr>
            <tr><td><code>PrivilegedAssignmentSchedule.Read.AzureADGroup</code></td><td>Active memberships of PIM-managed groups.</td></tr>
            <tr><td><code>PrivilegedEligibilitySchedule.Read.AzureADGroup</code></td><td>Eligible memberships of PIM-managed groups.</td></tr>
            <tr><td><code>GroupMember.Read.All</code></td><td>Expanding role-assigned groups, nested groups included.</td></tr>
            <tr><td><code>User.ReadBasic.All</code></td><td>Names instead of object ids.</td></tr>
          </tbody>
        </table>
      </div>
      <p>
        Tokens stay in memory for one run and are never written to disk. Nothing is written to your tenant.
        If your organization blocks Microsoft's Graph PowerShell client, pass any public client you own with
        <code>--client-id</code>; the guide explains
        <a href="https://github.com/FrodeHus/elevate/blob/main/docs/audit.md#5-a-tenant-that-blocks-the-graph-powershell-app">what to register</a>.
        The Elevate app registration is never involved, and its permissions do not change.
      </p>

      <h2 id="install">Install</h2>
      <p>macOS and Linux with Homebrew:</p>
      <pre><code>brew tap FrodeHus/elevate https://github.com/FrodeHus/elevate
brew trust frodehus/elevate
brew install frodehus/elevate/elevate-audit</code></pre>
      <p>
        Windows: <code>winget install Reothor.Elevate.Audit</code> once the manifest is published. On any
        platform, the <a href="https://github.com/FrodeHus/elevate/releases/latest">latest release</a> has
        <code>elevate-audit-&lt;version&gt;-&lt;platform&gt;</code> archives with SHA-256 checksums; unpack the
        binary anywhere on your PATH.
      </p>
      <p>
        You need a role that can read PIM tenant-wide: Global Reader, Privileged Role Administrator or
        Security Reader, and Reader on the Azure scopes you want covered. Details, options and the rule
        reference are in <a href="https://github.com/FrodeHus/elevate/blob/main/docs/audit.md">docs/audit.md</a>.
      </p>
    </main>

    <footer class="site-footer wrap">
      <div class="footer-top">
        <a class="brand" href="index.html"
          ><img src="assets/icon.png" width="28" height="28" alt="" /><span>Elevate</span></a
        >
        <p>Entra and Azure access, closer to your work.</p>
        <a class="text-link" href="https://github.com/FrodeHus/elevate">Give Elevate a star <span aria-hidden="true">↗</span></a>
      </div>
      <div class="footer-bottom">
        <span>Made by <a href="https://github.com/FrodeHus">Frode Hus</a>. Open source under the
          <a href="https://github.com/FrodeHus/elevate/blob/main/LICENSE">MIT license</a>.</span>
        <div>
          <a href="index.html">Product page</a>
          <a href="terms.html">Terms of Service</a>
          <a href="privacy.html">Privacy Statement</a>
          <a href="https://github.com/FrodeHus/elevate/issues">Feedback</a>
        </div>
      </div>
    </footer>
  </body>
</html>
```

If `.hero-intro`, `.button`, `.button-small`, `.text-link`, `.nav-links`, `.site-footer`, `.footer-top` or `.footer-bottom` render differently than expected, check the class names against `site/index.html` and `site/styles.css` and use the ones that exist; do not add new global CSS to `styles.css`.

- [ ] **Step 2: Link it from the product page**

In `site/index.html`, in the `nav-links` div, append `<a href="audit.html">Audit</a>` after the Guides link. In the footer's link group, add `<a href="audit.html">Audit tool</a>` before the Terms link.

- [ ] **Step 3: Validate and look**

```bash
python3 scripts/check-site.py
python3 -m http.server 4173 --directory site
```

Expected: `Site valid: 6 pages …`. Open <http://localhost:4173/audit.html> at desktop width and at 390px: navigation wraps, the `<pre>` scrolls horizontally instead of the page, the sample link opens `audit-sample.html`. Stop the server.

- [ ] **Step 4: Commit**

```bash
git add site/audit.html site/index.html
git commit -m "site: elevate-audit page and links"
```

---

### Task 20: CI, release, formula and winget

**Files:**
- Create: `.github/workflows/audit.yml`, `scripts/update-audit-formula.sh`, `audit/winget/New-Manifest.ps1`, `audit/winget/templates/Reothor.Elevate.Audit.yaml`, `audit/winget/templates/Reothor.Elevate.Audit.installer.yaml`, `audit/winget/templates/Reothor.Elevate.Audit.locale.en-US.yaml`
- Modify: `.github/workflows/release.yml`

**Interfaces:** `scripts/update-audit-formula.sh <version> <owner/repo> <dist dir>` writes `Formula/elevate-audit.rb`.

- [ ] **Step 1: CI workflow**

Copy `.github/workflows/cli.yml` to `.github/workflows/audit.yml` and change: `name: Audit`; the `paths` list to `["audit/**", "windows/src/Elevate.Core/**", ".github/workflows/audit.yml"]`; the grep in the `changes` job to `'^(audit/|windows/src/Elevate\.Core/|\.github/workflows/audit\.yml)'`; `global-json-file: audit/global.json`; every `cli/Elevate.Cli.sln` to `audit/Elevate.Audit.sln`; the publish step to:

```yaml
      - name: Publish and start the single-file binary
        shell: bash
        run: |
          ./audit/package.sh publish 0.0.0 "${{ matrix.rid }}"
          ./audit/package.sh archive 0.0.0 "${{ matrix.rid }}"
          exe="audit/dist/${{ matrix.rid }}/elevate-audit"; [ -f "$exe" ] || exe="$exe.exe"
          # Trimmed binary: exercise JSON both ways, the HTML renderer and the tables.
          "$exe" version
          "$exe" --from-snapshot audit/tests/Elevate.Audit.Tests/Fixtures/snapshots/sample.json --json - --quiet | grep -q '"findings"'
          "$exe" --from-snapshot audit/tests/Elevate.Audit.Tests/Fixtures/snapshots/sample.json --html "$RUNNER_TEMP/sample.html" --no-color --no-fail | grep -q "ENTRA-USER-PERMANENT"
          grep -q "<!doctype html>" "$RUNNER_TEMP/sample.html"
          ls -la audit/dist/*.sha256
```

and the artifact name to `audit-test-results-${{ matrix.name }}`.

- [ ] **Step 2: Formula script**

`scripts/update-audit-formula.sh` (`chmod +x`):

```bash
#!/usr/bin/env bash
# Regenerate Formula/elevate-audit.rb for a published release.
#
# Usage: scripts/update-audit-formula.sh <version> <owner/repo> <dist dir>
#   dist dir holds elevate-audit-<version>-<rid>.tar.gz.sha256 for osx-arm64, osx-x64, linux-x64, linux-arm64
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <version> <owner/repo> <dist dir>" >&2
  exit 2
fi

VERSION="$1"
REPO="$2"
DIST="$3"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/Formula/elevate-audit.rb"
mkdir -p "$(dirname "$OUT")"

sha() {
  local file="$DIST/elevate-audit-$VERSION-$1.tar.gz.sha256"
  test -f "$file" || { echo "missing $file" >&2; exit 1; }
  cut -d' ' -f1 "$file"
}

OSX_ARM="$(sha osx-arm64)"
OSX_X64="$(sha osx-x64)"
LINUX_X64="$(sha linux-x64)"
LINUX_ARM="$(sha linux-arm64)"

cat > "$OUT" <<EOF
class ElevateAudit < Formula
  desc "Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM"
  homepage "https://github.com/$REPO"
  version "$VERSION"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-osx-arm64.tar.gz"
      sha256 "$OSX_ARM"
    end
    on_intel do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-osx-x64.tar.gz"
      sha256 "$OSX_X64"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-linux-arm64.tar.gz"
      sha256 "$LINUX_ARM"
    end
    on_intel do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-linux-x64.tar.gz"
      sha256 "$LINUX_X64"
    end
  end

  def install
    bin.install "elevate-audit"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate-audit version")
  end
end
EOF

echo "Wrote $OUT (version $VERSION)"
```

Dry-run it:

```bash
d=$(mktemp -d); for r in osx-arm64 osx-x64 linux-x64 linux-arm64; do echo "0000000000000000000000000000000000000000000000000000000000000000  x" > "$d/elevate-audit-9.9.9-$r.tar.gz.sha256"; done
./scripts/update-audit-formula.sh 9.9.9 FrodeHus/elevate "$d" && ruby -c Formula/elevate-audit.rb && git checkout -- Formula 2>/dev/null; rm -f Formula/elevate-audit.rb
```

Expected: "Wrote …", "Syntax OK". The generated file is not committed; the release run creates it.

- [ ] **Step 3: winget manifests**

Copy `cli/winget/New-Manifest.ps1` to `audit/winget/New-Manifest.ps1` and replace every `Elevate.CLI` with `Elevate.Audit`, `elevate-cli-` with `elevate-audit-`, and the three template names. Templates in `audit/winget/templates/`:

`Reothor.Elevate.Audit.yaml`:

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.10.0.schema.json
PackageIdentifier: Reothor.Elevate.Audit
PackageVersion: {{VERSION}}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.10.0
```

`Reothor.Elevate.Audit.installer.yaml`:

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.10.0.schema.json
PackageIdentifier: Reothor.Elevate.Audit
PackageVersion: {{VERSION}}
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: elevate-audit.exe
  PortableCommandAlias: elevate-audit
Commands:
- elevate-audit
UpgradeBehavior: install
MinimumOSVersion: 10.0.17763.0
ReleaseDate: {{RELEASE_DATE}}
Installers:
- Architecture: x64
  InstallerUrl: {{X64_URL}}
  InstallerSha256: {{X64_SHA256}}
- Architecture: arm64
  InstallerUrl: {{ARM64_URL}}
  InstallerSha256: {{ARM64_SHA256}}
ManifestType: installer
ManifestVersion: 1.10.0
```

`Reothor.Elevate.Audit.locale.en-US.yaml`:

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.10.0.schema.json
PackageIdentifier: Reothor.Elevate.Audit
PackageVersion: {{VERSION}}
PackageLocale: en-US
Publisher: Reothor
PublisherUrl: https://github.com/FrodeHus
PublisherSupportUrl: https://github.com/FrodeHus/elevate/issues
Author: Frode Hus
PackageName: Elevate Audit
PackageUrl: https://github.com/FrodeHus/elevate
License: MIT
LicenseUrl: https://github.com/FrodeHus/elevate/blob/main/LICENSE
Copyright: Copyright (c) Frode Hus
ShortDescription: Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM
Description: |-
  A read-only companion to Elevate for administrators. Sign in with Microsoft's Graph PowerShell and
  Azure CLI public clients (no app registration), and get a list of permanent privileged Entra role
  assignments held by users and groups (nested groups resolved), permanent members of PIM-managed
  groups, and permanent Azure Owner and Contributor assignments, with remedies, as terminal output,
  JSON or a self-contained HTML report.
Moniker: elevate-audit
Tags:
- azure
- cli
- entra
- pim
- privileged-identity-management
- rbac
- security
ReleaseNotesUrl: https://github.com/FrodeHus/elevate/releases/tag/v{{VERSION}}
Documentations:
- DocumentLabel: Guide
  DocumentUrl: https://github.com/FrodeHus/elevate/blob/main/docs/audit.md
ManifestType: defaultLocale
ManifestVersion: 1.10.0
```

- [ ] **Step 4: Release workflow**

In `.github/workflows/release.yml`:

1. Duplicate the whole `cli:` job as `audit:` directly after it, with these substitutions: job `name: Audit (${{ matrix.name }})`; `global-json-file: audit/global.json`; `dotnet test audit/Elevate.Audit.sln -c Release`; `./audit/package.sh publish` and `archive`; in the macOS signing loop `bin="audit/dist/$rid/elevate-audit"` (keep `--entitlements cli/elevate.entitlements`, the same hardened-runtime entitlements apply); in the Windows signing loop `"audit/dist/$rid/elevate-audit.exe"`; the winget step reads `audit/dist/elevate-audit-$env:VERSION-win-x64.zip.sha256` and `-win-arm64.zip.sha256`, runs `./audit/winget/New-Manifest.ps1 …` and `winget validate --manifest "audit/winget/manifests/r/Reothor/Elevate.Audit/$env:VERSION"`; artifact names `audit-${{ matrix.name }}` (paths `audit/dist/elevate-audit-*.tar.gz`, `*.zip`, `*.sha256`) and `winget-audit-manifest` (path `audit/winget/manifests`).
2. `publish.needs` becomes `[macos, windows, cli, audit]`.
3. After the `pattern: cli-*` download step add the same block with `pattern: audit-*`.
4. In "Versions and hashes", after the CLI checksums line add:
   ```bash
   (cd dist && for f in elevate-audit-*.sha256; do printf '%s  %s\n' "$(cut -d' ' -f1 "$f")" "${f%.sha256}"; done) > "dist/elevate-audit-${TAG#v}-checksums.txt"
   cat "dist/elevate-audit-${TAG#v}-checksums.txt"
   ```
5. In "Release notes", before `echo "## Enterprise"`, add:
   ```bash
   echo
   echo "## elevate-audit (Linux, macOS, Windows)"
   echo
   echo 'The read-only companion that lists standing privileged access to move to PIM: `brew install frodehus/elevate/elevate-audit`, or `winget install Reothor.Elevate.Audit` once the manifest is submitted, or download `elevate-audit-'"$VERSION"'-<platform>` below and put `elevate-audit` on your PATH. Verify with `sha256sum -c elevate-audit-'"$VERSION"'-checksums.txt`. See [docs/audit.md](https://github.com/'"${{ github.repository }}"'/blob/main/docs/audit.md).'
   ```
6. In "Create GitHub Release", after the CLI checksums line add:
   ```
   dist/elevate-audit-$VERSION-*.tar.gz
   dist/elevate-audit-$VERSION-*.zip
   dist/elevate-audit-$VERSION-*.sha256
   "dist/elevate-audit-$VERSION-checksums.txt"
   ```
7. In "Update the cask and the formula on main": after `./scripts/update-formula.sh …` add `./scripts/update-audit-formula.sh "$VERSION" "${{ github.repository }}" dist`; extend the `git diff --quiet --` list and the `git add` list with `Formula/elevate-audit.rb`; commit message `"Cask and formulas: Elevate $VERSION"`.

- [ ] **Step 5: Validate the YAML and scripts**

```bash
ruby -ryaml -e 'YAML.load_file(".github/workflows/audit.yml"); YAML.load_file(".github/workflows/release.yml"); puts "yaml ok"'
bash -n scripts/update-audit-formula.sh audit/package.sh
grep -c "elevate-audit" .github/workflows/release.yml
```

Expected: `yaml ok`, no syntax errors, and a count well above 10. Then push the branch and confirm the `Audit` workflow's three legs pass on the pull request before merging; the release job is exercised by the next `v*` tag.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/audit.yml .github/workflows/release.yml scripts/update-audit-formula.sh audit/winget
git commit -m "ci: build, test, sign and release elevate-audit; formula and winget manifest"
```

---

## Done criteria

- `dotnet test audit/Elevate.Audit.sln` green; `python3 scripts/check-site.py` green; the `Audit` workflow green on the PR.
- One manual run against the reothor.no tenant (`elevate-audit --save-snapshot ~/elevate-audit-reothor.json --html ~/elevate-audit-reothor.html`) recorded in the PR description: the consent prompt showed the six scopes, the report rendered, and any surprise was turned into an issue.
- PR from `audit-tool` to `main`, merged with the repository's PR rule; the next `v*` tag ships `elevate-audit`.
