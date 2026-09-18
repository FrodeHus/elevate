# Enterprise Co-Branding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an organization push a name and support contact through managed configuration so Elevate reads as "Elevate, by Contoso" with a route to their help desk, on macOS, Windows and the CLI.

**Architecture:** Four new managed keys join the existing seven in both cores (Swift `ElevateCore/Managed` is the source of truth; C# `Elevate.Core/Managed` is its documented port, shared by the Windows app and the CLI). A new `Branding` value type resolves those four raw fields into the exact strings each surface renders, so the phrasing rules live in one testable place per language instead of at twelve call sites. No new I/O of any kind.

**Tech Stack:** Swift 6 / SwiftUI (`swift test`), C# / .NET 10 / WinUI 3 (`dotnet test`), Python 3 stdlib (`scripts/validate-enterprise-kit.py`).

**Spec:** `docs/superpowers/specs/2026-09-18-enterprise-cobranding-design.md`

## Global Constraints

- **Key names, verbatim:** `OrganizationName`, `OrganizationTitleStyle`, `OrganizationSupportUrl`, `OrganizationSupportEmail`.
- **`OrganizationName` is 1–32 characters** after trimming. Over-length is **rejected with the actual length in the warning**, never truncated.
- **`OrganizationTitleStyle` accepts exactly `by`, `managedBy`, `none`**, matched case-insensitively. Absent + name present defaults to `by`.
- **`OrganizationSupportUrl` is `https://` only.** `http://` and every other scheme are rejected.
- **`OrganizationName` gates the family.** If it is absent or rejected, the other three are ignored, each with its own warning.
- **Per-key rejection.** A bad value appends to `warnings`, stays out of `keysInEffect`, and never fails the load or affects another key.
- **Unbranded renders identically to today.** Every UI change is behind `Branding? != nil`.
- **Copy, verbatim:** header caption `by Contoso` / `Managed by Contoso`; first run `Provided by Contoso.`; CLI failures `Need help? Contoso IT — https://help.contoso.com` (em dash, U+2014).
- **Both cores change together.** A Swift change without its C# port is an incomplete task.
- **Baselines to keep green:** Swift 350 tests, `Elevate.Core.Tests` 434, `Elevate.Cli.Tests` 164, validator "all templates consistent".

---

### Task 1: Swift core — the four keys and their validation

**Files:**
- Modify: `macos/Sources/ElevateCore/Managed/ManagedSources.swift:5-13` (the `ManagedKey` enum)
- Modify: `macos/Sources/ElevateCore/Managed/ManagedConfiguration.swift` (fields + `load(from:)`)
- Test: `macos/Tests/ElevateCoreTests/ManagedConfigurationTests.swift`

**Interfaces:**
- Consumes: `ManagedConfigurationSource.string(_:)`, the existing `ManagedConfiguration.load(from:)` shape.
- Produces: `ManagedKey.organizationName / .organizationTitleStyle / .organizationSupportUrl / .organizationSupportEmail`; `OrganizationTitleStyle` enum with cases `by`, `managedBy`, `none`; `ManagedConfiguration.organizationName: String?`, `.organizationTitleStyle: OrganizationTitleStyle?`, `.organizationSupportUrl: URL?`, `.organizationSupportEmail: String?`.

- [ ] **Step 1: Write the failing tests**

Add to `macos/Tests/ElevateCoreTests/ManagedConfigurationTests.swift` (follow the existing `DictionaryManagedSource` usage in that file):

```swift
@Test func brandingKeysLoad() {
    let config = ManagedConfiguration.load(from: DictionaryManagedSource([
        "OrganizationName": "Contoso",
        "OrganizationTitleStyle": "managedBy",
        "OrganizationSupportUrl": "https://help.contoso.com",
        "OrganizationSupportEmail": "it@contoso.com",
    ]))
    #expect(config.organizationName == "Contoso")
    #expect(config.organizationTitleStyle == .managedBy)
    #expect(config.organizationSupportUrl?.absoluteString == "https://help.contoso.com")
    #expect(config.organizationSupportEmail == "it@contoso.com")
    #expect(config.warnings.isEmpty)
    #expect(config.keysInEffect.contains(.organizationName))
}

@Test func organizationNameDefaultsToNoStyle() {
    let config = ManagedConfiguration.load(from: DictionaryManagedSource(["OrganizationName": "Contoso"]))
    #expect(config.organizationName == "Contoso")
    #expect(config.organizationTitleStyle == nil)
}

@Test func organizationNameIsTrimmedAndBoundedAt32() {
    let ok = String(repeating: "a", count: 32)
    let tooLong = String(repeating: "a", count: 33)
    let good = ManagedConfiguration.load(from: DictionaryManagedSource(["OrganizationName": "  Contoso  "]))
    #expect(good.organizationName == "Contoso")
    #expect(ManagedConfiguration.load(from: DictionaryManagedSource(["OrganizationName": ok])).organizationName == ok)
    let over = ManagedConfiguration.load(from: DictionaryManagedSource(["OrganizationName": tooLong]))
    #expect(over.organizationName == nil)
    #expect(over.warnings.contains { $0.contains("33 characters") })
}

@Test func blankOrganizationNameIsRejected() {
    let config = ManagedConfiguration.load(from: DictionaryManagedSource(["OrganizationName": "   "]))
    #expect(config.organizationName == nil)
    #expect(config.warnings.count == 1)
}

@Test func titleStyleIsCaseInsensitiveAndValidated() {
    let ok = ManagedConfiguration.load(from: DictionaryManagedSource([
        "OrganizationName": "Contoso", "OrganizationTitleStyle": "MANAGEDBY",
    ]))
    #expect(ok.organizationTitleStyle == .managedBy)
    let bad = ManagedConfiguration.load(from: DictionaryManagedSource([
        "OrganizationName": "Contoso", "OrganizationTitleStyle": "sponsoredBy",
    ]))
    #expect(bad.organizationTitleStyle == nil)
    #expect(bad.warnings.contains { $0.contains("by, managedBy, none") })
}

@Test func supportUrlRejectsNonHttps() {
    let config = ManagedConfiguration.load(from: DictionaryManagedSource([
        "OrganizationName": "Contoso", "OrganizationSupportUrl": "http://help.contoso.com",
    ]))
    #expect(config.organizationSupportUrl == nil)
    #expect(config.warnings.contains { $0.contains("only https URLs are accepted") })
}

@Test func supportEmailIsValidated() {
    let config = ManagedConfiguration.load(from: DictionaryManagedSource([
        "OrganizationName": "Contoso", "OrganizationSupportEmail": "not an email",
    ]))
    #expect(config.organizationSupportEmail == nil)
    #expect(config.warnings.count == 1)
}

@Test func brandingKeysAreIgnoredWithoutAnOrganizationName() {
    let config = ManagedConfiguration.load(from: DictionaryManagedSource([
        "OrganizationTitleStyle": "by",
        "OrganizationSupportUrl": "https://help.contoso.com",
        "OrganizationSupportEmail": "it@contoso.com",
    ]))
    #expect(config.organizationTitleStyle == nil)
    #expect(config.organizationSupportUrl == nil)
    #expect(config.organizationSupportEmail == nil)
    #expect(config.warnings.count == 3)
    #expect(config.keysInEffect.isEmpty)
}

@Test func unbrandedConfigurationIsUnchanged() {
    let config = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": "11111111-2222-3333-4444-555555555555"]))
    #expect(config.organizationName == nil)
    #expect(config.warnings.isEmpty)
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd macos && swift test --filter ManagedConfigurationTests`
Expected: FAIL — `value of type 'ManagedConfiguration' has no member 'organizationName'`.

- [ ] **Step 3: Add the keys and the style enum**

In `ManagedSources.swift`, append to `ManagedKey` (order matters — `keysInEffect` is documented as being in `allCases` order, and the name must be read first):

```swift
    case organizationName = "OrganizationName"
    case organizationTitleStyle = "OrganizationTitleStyle"
    case organizationSupportUrl = "OrganizationSupportUrl"
    case organizationSupportEmail = "OrganizationSupportEmail"
```

Add to the same file, below the enum:

```swift
/// How an organization's name is phrased beside Elevate's own. `none` keeps the support contact
/// and the About section but renders no caption in the panel header.
public enum OrganizationTitleStyle: String, CaseIterable, Hashable, Sendable {
    case by
    case managedBy
    case none
}
```

- [ ] **Step 4: Add the fields and validation**

In `ManagedConfiguration.swift`, add after `managedProfilesUrl`:

```swift
    public var organizationName: String?
    public var organizationTitleStyle: OrganizationTitleStyle?
    public var organizationSupportUrl: URL?
    public var organizationSupportEmail: String?
```

Initialise all four to `nil` in `init()`. Then append to `load(from:)`, after the `managedProfilesUrl` block and before the `if !config.keysInEffect.isEmpty` block:

```swift
        if let raw = source.string(.organizationName) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.isEmpty {
                config.warnings.append("OrganizationName: a blank name is ignored")
            } else if trimmed.count > 32 {
                config.warnings.append("OrganizationName: '\(trimmed)' is \(trimmed.count) characters; the maximum is 32")
            } else {
                config.organizationName = trimmed
                config.keysInEffect.append(.organizationName)
            }
        }

        // The three keys below decorate the name; without one they have nothing to attach to.
        let hasName = config.organizationName != nil

        if let raw = source.string(.organizationTitleStyle) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !hasName {
                config.warnings.append("OrganizationTitleStyle: ignored because OrganizationName is not set")
            } else if let style = OrganizationTitleStyle.allCases.first(where: { $0.rawValue.lowercased() == trimmed.lowercased() }) {
                config.organizationTitleStyle = style
                config.keysInEffect.append(.organizationTitleStyle)
            } else {
                config.warnings.append("OrganizationTitleStyle: '\(raw)' is not one of by, managedBy, none")
            }
        }

        if let raw = source.string(.organizationSupportUrl) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !hasName {
                config.warnings.append("OrganizationSupportUrl: ignored because OrganizationName is not set")
            } else if let url = URL(string: trimmed), url.scheme == "https" {
                config.organizationSupportUrl = url
                config.keysInEffect.append(.organizationSupportUrl)
            } else {
                config.warnings.append("OrganizationSupportUrl: only https URLs are accepted")
            }
        }

        if let raw = source.string(.organizationSupportEmail) {
            let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if !hasName {
                config.warnings.append("OrganizationSupportEmail: ignored because OrganizationName is not set")
            } else if trimmed.count >= 3, trimmed.contains("@"), !trimmed.contains(where: \.isWhitespace) {
                config.organizationSupportEmail = trimmed
                config.keysInEffect.append(.organizationSupportEmail)
            } else {
                config.warnings.append("OrganizationSupportEmail: '\(raw)' is not an email address")
            }
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd macos && swift test`
Expected: PASS — 350 existing plus the 9 new, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add macos/Sources/ElevateCore/Managed/ManagedSources.swift macos/Sources/ElevateCore/Managed/ManagedConfiguration.swift macos/Tests/ElevateCoreTests/ManagedConfigurationTests.swift
git commit -m "macOS core: read and validate the four organization branding keys"
```

---

### Task 2: Swift core — the `Branding` type

**Files:**
- Create: `macos/Sources/ElevateCore/Managed/Branding.swift`
- Create: `macos/Tests/ElevateCoreTests/BrandingTests.swift`

**Interfaces:**
- Consumes: `ManagedConfiguration.organizationName / .organizationTitleStyle / .organizationSupportUrl / .organizationSupportEmail`, `OrganizationTitleStyle` (Task 1).
- Produces: `Branding.resolve(from: ManagedConfiguration) -> Branding?`, with `headerCaption: String?`, `firstRunLine: String`, `supportLabel: String`, `supportLine: String?`, `hasSupport: Bool`, `supportUrl: URL?`, `supportEmail: String?`, `organizationName: String`.

- [ ] **Step 1: Write the failing tests**

Create `macos/Tests/ElevateCoreTests/BrandingTests.swift`:

```swift
import Testing
@testable import ElevateCore

struct BrandingTests {
    private func config(_ values: [String: String]) -> ManagedConfiguration {
        ManagedConfiguration.load(from: DictionaryManagedSource(values))
    }

    @Test func unbrandedResolvesToNil() {
        #expect(Branding.resolve(from: .none) == nil)
        #expect(Branding.resolve(from: config(["ClientId": "11111111-2222-3333-4444-555555555555"])) == nil)
    }

    @Test func defaultStyleIsBy() {
        let branding = Branding.resolve(from: config(["OrganizationName": "Contoso"]))
        #expect(branding?.headerCaption == "by Contoso")
    }

    @Test func managedByStyleCaption() {
        let branding = Branding.resolve(from: config([
            "OrganizationName": "Contoso", "OrganizationTitleStyle": "managedBy",
        ]))
        #expect(branding?.headerCaption == "Managed by Contoso")
    }

    @Test func noneStyleSuppressesTheCaptionOnly() {
        let branding = Branding.resolve(from: config([
            "OrganizationName": "Contoso",
            "OrganizationTitleStyle": "none",
            "OrganizationSupportUrl": "https://help.contoso.com",
        ]))
        #expect(branding?.headerCaption == nil)
        #expect(branding?.firstRunLine == "Provided by Contoso.")
        #expect(branding?.hasSupport == true)
    }

    @Test func supportLinePrefersTheUrl() {
        let both = Branding.resolve(from: config([
            "OrganizationName": "Contoso",
            "OrganizationSupportUrl": "https://help.contoso.com",
            "OrganizationSupportEmail": "it@contoso.com",
        ]))
        #expect(both?.supportLine == "Need help? Contoso IT — https://help.contoso.com")

        let emailOnly = Branding.resolve(from: config([
            "OrganizationName": "Contoso", "OrganizationSupportEmail": "it@contoso.com",
        ]))
        #expect(emailOnly?.supportLine == "Need help? Contoso IT — it@contoso.com")
    }

    @Test func noSupportMeansNoSupportLine() {
        let branding = Branding.resolve(from: config(["OrganizationName": "Contoso"]))
        #expect(branding?.hasSupport == false)
        #expect(branding?.supportLine == nil)
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd macos && swift test --filter BrandingTests`
Expected: FAIL — `cannot find 'Branding' in scope`.

- [ ] **Step 3: Write the implementation**

Create `macos/Sources/ElevateCore/Managed/Branding.swift`:

```swift
import Foundation

/// The organization's co-branding, resolved from `ManagedConfiguration` into the exact strings each
/// surface renders. Resolving to `nil` is the unbranded case: every caller renders exactly what it
/// rendered before this type existed, so an unbranded install is unaffected by co-branding entirely.
///
/// Elevate's own name is never replaced — the organization's name is added beside it. See
/// `docs/superpowers/specs/2026-09-18-enterprise-cobranding-design.md` §1.
public struct Branding: Hashable, Sendable {
    public let organizationName: String
    public let titleStyle: OrganizationTitleStyle
    public let supportUrl: URL?
    public let supportEmail: String?

    /// The panel header's second line, under "Elevate". `nil` for the `none` style.
    public var headerCaption: String? {
        switch titleStyle {
        case .by: "by \(organizationName)"
        case .managedBy: "Managed by \(organizationName)"
        case .none: nil
        }
    }

    public var firstRunLine: String { "Provided by \(organizationName)." }

    public var supportLabel: String { "\(organizationName) IT" }

    public var hasSupport: Bool { supportUrl != nil || supportEmail != nil }

    /// The one-line contact appended to CLI failures. Prefers the URL: a help desk portal lists the
    /// address, but an address does not list the portal.
    public var supportLine: String? {
        guard let target = supportUrl?.absoluteString ?? supportEmail else { return nil }
        return "Need help? \(supportLabel) — \(target)"
    }

    public static func resolve(from config: ManagedConfiguration) -> Branding? {
        guard let name = config.organizationName else { return nil }
        return Branding(
            organizationName: name,
            titleStyle: config.organizationTitleStyle ?? .by,
            supportUrl: config.organizationSupportUrl,
            supportEmail: config.organizationSupportEmail
        )
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd macos && swift test`
Expected: PASS, 0 failures.

- [ ] **Step 5: Commit**

```bash
git add macos/Sources/ElevateCore/Managed/Branding.swift macos/Tests/ElevateCoreTests/BrandingTests.swift
git commit -m "macOS core: resolve managed branding keys into a Branding value"
```

---

### Task 3: C# core — the four keys and their validation

**Files:**
- Modify: `windows/src/Elevate.Core/Managed/ManagedKey.cs`
- Modify: `windows/src/Elevate.Core/Managed/ManagedConfiguration.cs`
- Test: `windows/tests/Elevate.Core.Tests/Managed/ManagedConfigurationTests.cs`

**Interfaces:**
- Consumes: `IManagedConfigurationSource.String(ManagedKey)`, the existing `ManagedConfiguration.Load` shape.
- Produces: `ManagedKey.OrganizationName / .OrganizationTitleStyle / .OrganizationSupportUrl / .OrganizationSupportEmail`; `OrganizationTitleStyle` enum (`By`, `ManagedBy`, `None`) with wire names `by`, `managedBy`, `none`; `ManagedConfiguration.OrganizationName: string?`, `.OrganizationTitleStyle: OrganizationTitleStyle?`, `.OrganizationSupportUrl: Uri?`, `.OrganizationSupportEmail: string?`.

This is the port of Tasks 1 and 2's validation. Every rule, warning string and boundary must match the Swift exactly — the Swift is the source of truth.

- [ ] **Step 1: Write the failing tests**

Add to `windows/tests/Elevate.Core.Tests/Managed/ManagedConfigurationTests.cs` (follow the existing `DictionaryManagedSource` usage in that file):

```csharp
[Fact]
public void BrandingKeysLoad()
{
    var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = "Contoso",
        ["OrganizationTitleStyle"] = "managedBy",
        ["OrganizationSupportUrl"] = "https://help.contoso.com",
        ["OrganizationSupportEmail"] = "it@contoso.com",
    }));

    Assert.Equal("Contoso", config.OrganizationName);
    Assert.Equal(OrganizationTitleStyle.ManagedBy, config.OrganizationTitleStyle);
    Assert.Equal("https://help.contoso.com/", config.OrganizationSupportUrl?.ToString());
    Assert.Equal("it@contoso.com", config.OrganizationSupportEmail);
    Assert.Empty(config.Warnings);
}

[Fact]
public void OrganizationNameIsBoundedAt32()
{
    var over = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = new string('a', 33),
    }));
    Assert.Null(over.OrganizationName);
    Assert.Contains(over.Warnings, w => w.Contains("33 characters"));

    var exact = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = new string('a', 32),
    }));
    Assert.NotNull(exact.OrganizationName);
}

[Fact]
public void TitleStyleIsCaseInsensitiveAndValidated()
{
    var ok = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = "Contoso",
        ["OrganizationTitleStyle"] = "MANAGEDBY",
    }));
    Assert.Equal(OrganizationTitleStyle.ManagedBy, ok.OrganizationTitleStyle);

    var bad = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = "Contoso",
        ["OrganizationTitleStyle"] = "sponsoredBy",
    }));
    Assert.Null(bad.OrganizationTitleStyle);
    Assert.Contains(bad.Warnings, w => w.Contains("by, managedBy, none"));
}

[Fact]
public void SupportUrlRejectsNonHttps()
{
    var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = "Contoso",
        ["OrganizationSupportUrl"] = "http://help.contoso.com",
    }));
    Assert.Null(config.OrganizationSupportUrl);
    Assert.Contains(config.Warnings, w => w.Contains("only https URLs are accepted"));
}

[Fact]
public void BrandingKeysAreIgnoredWithoutAnOrganizationName()
{
    var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationTitleStyle"] = "by",
        ["OrganizationSupportUrl"] = "https://help.contoso.com",
        ["OrganizationSupportEmail"] = "it@contoso.com",
    }));
    Assert.Null(config.OrganizationTitleStyle);
    Assert.Null(config.OrganizationSupportUrl);
    Assert.Null(config.OrganizationSupportEmail);
    Assert.Equal(3, config.Warnings.Count);
    Assert.Empty(config.KeysInEffect);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj --nologo`
Expected: FAIL — compile error, `ManagedConfiguration` does not contain `OrganizationName`.

- [ ] **Step 3: Add the keys and the style enum**

In `ManagedKey.cs`, append the four members to the `ManagedKey` enum, add their four `Name(...)` switch arms returning the exact strings, and append them to `ManagedKeys.All` — all in the same order as Swift. Add to the same file:

```csharp
/// <summary>
/// How an organization's name is phrased beside Elevate's own. <c>None</c> keeps the support
/// contact and the About section but renders no caption in the panel header. Port of the Swift
/// <c>OrganizationTitleStyle</c> enum; the wire names are <c>by</c>, <c>managedBy</c>, <c>none</c>.
/// </summary>
public enum OrganizationTitleStyle
{
    By,
    ManagedBy,
    None,
}

public static class OrganizationTitleStyles
{
    public static string Name(this OrganizationTitleStyle style) => style switch
    {
        OrganizationTitleStyle.By => "by",
        OrganizationTitleStyle.ManagedBy => "managedBy",
        OrganizationTitleStyle.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, message: null),
    };

    /// <summary>Case-insensitive lookup by wire name; null when no style matches.</summary>
    public static OrganizationTitleStyle? Parse(string raw) =>
        Enum.GetValues<OrganizationTitleStyle>()
            .Cast<OrganizationTitleStyle?>()
            .FirstOrDefault(s => string.Equals(s!.Value.Name(), raw, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Add the properties and validation**

In `ManagedConfiguration.cs` add four `init` properties mirroring Task 1's Swift fields, declare their locals in `Load`, and add the validation after the `ManagedProfilesUrl` block, using the **same warning strings as Swift**:

```csharp
        var rawOrganizationName = source.String(ManagedKey.OrganizationName);
        if (rawOrganizationName is not null)
        {
            var trimmed = rawOrganizationName.Trim();
            if (trimmed.Length == 0)
            {
                warnings.Add("OrganizationName: a blank name is ignored");
            }
            else if (trimmed.Length > 32)
            {
                warnings.Add($"OrganizationName: '{trimmed}' is {trimmed.Length} characters; the maximum is 32");
            }
            else
            {
                organizationName = trimmed;
                keysInEffect.Add(ManagedKey.OrganizationName);
            }
        }

        // The three keys below decorate the name; without one they have nothing to attach to.
        var hasName = organizationName is not null;

        var rawTitleStyle = source.String(ManagedKey.OrganizationTitleStyle);
        if (rawTitleStyle is not null)
        {
            if (!hasName)
            {
                warnings.Add("OrganizationTitleStyle: ignored because OrganizationName is not set");
            }
            else if (OrganizationTitleStyles.Parse(rawTitleStyle.Trim()) is { } style)
            {
                organizationTitleStyle = style;
                keysInEffect.Add(ManagedKey.OrganizationTitleStyle);
            }
            else
            {
                warnings.Add($"OrganizationTitleStyle: '{rawTitleStyle}' is not one of by, managedBy, none");
            }
        }

        var rawSupportUrl = source.String(ManagedKey.OrganizationSupportUrl);
        if (rawSupportUrl is not null)
        {
            if (!hasName)
            {
                warnings.Add("OrganizationSupportUrl: ignored because OrganizationName is not set");
            }
            else if (Uri.TryCreate(rawSupportUrl.Trim(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
            {
                organizationSupportUrl = url;
                keysInEffect.Add(ManagedKey.OrganizationSupportUrl);
            }
            else
            {
                warnings.Add("OrganizationSupportUrl: only https URLs are accepted");
            }
        }

        var rawSupportEmail = source.String(ManagedKey.OrganizationSupportEmail);
        if (rawSupportEmail is not null)
        {
            var trimmed = rawSupportEmail.Trim();
            if (!hasName)
            {
                warnings.Add("OrganizationSupportEmail: ignored because OrganizationName is not set");
            }
            else if (trimmed.Length >= 3 && trimmed.Contains('@') && !trimmed.Any(char.IsWhiteSpace))
            {
                organizationSupportEmail = trimmed;
                keysInEffect.Add(ManagedKey.OrganizationSupportEmail);
            }
            else
            {
                warnings.Add($"OrganizationSupportEmail: '{rawSupportEmail}' is not an email address");
            }
        }
```

Assign all four into the returned record.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj --nologo`
Expected: PASS — 434 existing plus the 5 new, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add windows/src/Elevate.Core/Managed/ManagedKey.cs windows/src/Elevate.Core/Managed/ManagedConfiguration.cs windows/tests/Elevate.Core.Tests/Managed/ManagedConfigurationTests.cs
git commit -m "Windows core: read and validate the four organization branding keys"
```

---

### Task 4: C# core — the `Branding` record

**Files:**
- Create: `windows/src/Elevate.Core/Managed/Branding.cs`
- Create: `windows/tests/Elevate.Core.Tests/Managed/BrandingTests.cs`

**Interfaces:**
- Consumes: Task 3's four properties and `OrganizationTitleStyle`.
- Produces: `Branding.Resolve(ManagedConfiguration) -> Branding?` with `OrganizationName`, `TitleStyle`, `SupportUrl`, `SupportEmail`, `HeaderCaption: string?`, `FirstRunLine: string`, `SupportLabel: string`, `HasSupport: bool`, `SupportLine: string?`.

- [ ] **Step 1: Write the failing tests**

Create `windows/tests/Elevate.Core.Tests/Managed/BrandingTests.cs` with the C# equivalents of every test in Task 2 — `UnbrandedResolvesToNull`, `DefaultStyleIsBy`, `ManagedByStyleCaption`, `NoneStyleSuppressesTheCaptionOnly`, `SupportLinePrefersTheUrl`, `NoSupportMeansNoSupportLine` — asserting the identical strings:

```csharp
[Fact]
public void SupportLinePrefersTheUrl()
{
    var config = ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = "Contoso",
        ["OrganizationSupportUrl"] = "https://help.contoso.com",
        ["OrganizationSupportEmail"] = "it@contoso.com",
    }));
    Assert.Equal("Need help? Contoso IT — https://help.contoso.com/", Branding.Resolve(config)!.SupportLine);
}
```

Note the trailing slash: `Uri.ToString()` normalises `https://help.contoso.com` to `https://help.contoso.com/`. Assert what `Uri` actually produces rather than fighting it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj --nologo`
Expected: FAIL — `Branding` does not exist.

- [ ] **Step 3: Write the implementation**

Create `windows/src/Elevate.Core/Managed/Branding.cs` as the port of Task 2's `Branding.swift`, same doc comment intent, same strings:

```csharp
namespace Elevate.Core.Managed;

/// <summary>
/// The organization's co-branding, resolved from <see cref="ManagedConfiguration"/> into the exact
/// strings each surface renders. Resolving to null is the unbranded case. Elevate's own name is
/// never replaced. Port of the Swift <c>Branding</c> struct.
/// </summary>
public sealed record Branding(
    string OrganizationName,
    OrganizationTitleStyle TitleStyle,
    Uri? SupportUrl,
    string? SupportEmail)
{
    /// <summary>The panel header's second line, under "Elevate". Null for the None style.</summary>
    public string? HeaderCaption => TitleStyle switch
    {
        OrganizationTitleStyle.By => $"by {OrganizationName}",
        OrganizationTitleStyle.ManagedBy => $"Managed by {OrganizationName}",
        _ => null,
    };

    public string FirstRunLine => $"Provided by {OrganizationName}.";

    public string SupportLabel => $"{OrganizationName} IT";

    public bool HasSupport => SupportUrl is not null || SupportEmail is not null;

    /// <summary>
    /// The one-line contact appended to CLI failures. Prefers the URL: a help desk portal lists the
    /// address, but an address does not list the portal.
    /// </summary>
    public string? SupportLine =>
        (SupportUrl?.ToString() ?? SupportEmail) is { } target
            ? $"Need help? {SupportLabel} — {target}"
            : null;

    public static Branding? Resolve(ManagedConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.OrganizationName is not { } name
            ? null
            : new Branding(name, config.OrganizationTitleStyle ?? OrganizationTitleStyle.By, config.OrganizationSupportUrl, config.OrganizationSupportEmail);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj --nologo`
Expected: PASS, 0 failures.

- [ ] **Step 5: Commit**

```bash
git add windows/src/Elevate.Core/Managed/Branding.cs windows/tests/Elevate.Core.Tests/Managed/BrandingTests.cs
git commit -m "Windows core: resolve managed branding keys into a Branding value"
```

---

### Task 5: macOS UI — header, first run, Settings, Diagnostics

**Files:**
- Modify: `macos/Sources/ElevateApp/App/AppModel+Managed.swift` (expose `branding`)
- Modify: `macos/Sources/ElevateApp/Views/PanelView.swift:217-222` (the `header` computed property)
- Modify: `macos/Sources/ElevateApp/Views/SetupView.swift:13-14`
- Modify: `macos/Sources/ElevateApp/Views/ManagedSection.swift` (Settings/About + Diagnostics)
- Test: `macos/Tests/ElevateAppTests/AppModelManagedTests.swift`

**Interfaces:**
- Consumes: `Branding.resolve(from:)` (Task 2).
- Produces: `AppModel.branding: Branding?`.

- [ ] **Step 1: Write the failing test**

Add to `macos/Tests/ElevateAppTests/AppModelManagedTests.swift`:

```swift
@Test func brandingIsExposedFromManagedConfiguration() async {
    let model = await AppModel.testModel(managed: ManagedConfiguration.load(from: DictionaryManagedSource([
        "OrganizationName": "Contoso", "OrganizationTitleStyle": "managedBy",
    ])))
    #expect(await model.branding?.headerCaption == "Managed by Contoso")
}

@Test func unbrandedModelHasNoBranding() async {
    let model = await AppModel.testModel(managed: .none)
    #expect(await model.branding == nil)
}
```

Use whatever test-model helper `AppModelManagedTests.swift` already uses; do not invent a new one.

- [ ] **Step 2: Run to verify it fails**

Run: `cd macos && swift test --filter AppModelManagedTests`
Expected: FAIL — no member `branding`.

- [ ] **Step 3: Expose branding on the model**

In `AppModel+Managed.swift`:

```swift
    /// The organization's co-branding, or nil when nothing is pushed. Every branded view is behind
    /// this being non-nil, so an unbranded install renders exactly as it did before.
    var branding: Branding? { Branding.resolve(from: managed) }
```

Use the actual name of the stored `ManagedConfiguration` on `AppModel` in place of `managed`.

- [ ] **Step 4: Render the header caption**

In `PanelView.swift`, replace the single title `Text("Elevate")` inside `header` with a two-line stack. The caption is the only addition; everything else in the row is untouched:

```swift
            VStack(alignment: .leading, spacing: 0) {
                Text("Elevate").font(.headline)
                if let caption = model.branding?.headerCaption {
                    Text(caption)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
```

- [ ] **Step 5: Render the first-run line and the About section**

In `SetupView.swift`, directly under `Text("Complete initial setup").font(.headline)`:

```swift
            if let branding = model.branding {
                Text(branding.firstRunLine).font(.subheadline).foregroundStyle(.secondary)
                if let url = branding.supportUrl {
                    Link("Get help from \(branding.supportLabel)", destination: url)
                        .font(.caption)
                } else if let email = branding.supportEmail, let url = URL(string: "mailto:\(email)") {
                    Link("Get help from \(branding.supportLabel)", destination: url)
                        .font(.caption)
                }
            }
```

In `ManagedSection.swift`, add a section rendered only when `model.branding != nil`, headed by the organization name, carrying the same support link and the email as a `mailto:`. Follow the existing section's layout in that file. Add the four resolved values and any branding warnings to the Diagnostics output that file already produces.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `cd macos && swift test`
Expected: PASS, 0 failures.

- [ ] **Step 7: Verify the header height by eye**

Build and render the panel offscreen via the repo's hosted-test screenshot path, once unbranded and once with `OrganizationName` set, and confirm the caption does not push the panel past its popover height or disturb the pinned section headers (spec §7). If it does, reduce the caption to `.caption2` with `spacing: -1`, or raise the panel height constant — and say which was needed.

- [ ] **Step 8: Commit**

```bash
git add macos/Sources/ElevateApp macos/Tests/ElevateAppTests/AppModelManagedTests.swift
git commit -m "macOS: show organization co-branding in the header, first run and About"
```

---

### Task 6: Windows UI — header, first run, Settings, Diagnostics

**Files:**
- Modify: `windows/src/Elevate.App/Views/PanelView.xaml:213-221` and its code-behind
- Modify: `windows/src/Elevate.App/Views/PanelView.xaml:295-305` (the first-run block)
- Modify: `windows/src/Elevate.App/Views/SettingsWindow.xaml` and its code-behind
- Test: `windows/tests/Elevate.App.Tests/ManagedSettingsTests.cs`

**Interfaces:**
- Consumes: `Branding.Resolve` (Task 4).
- Produces: a `Branding?` property on the same view-model `PanelView` already binds to.

This is Task 5's design on WinUI: `TextBlock Text="Elevate"` in column 0 becomes a two-row `StackPanel` whose second `TextBlock` binds the caption with `Visibility` driven by it being non-null, `TextTrimming="CharacterEllipsis"`; the first-run block gains the provided-by line and support link; Settings gains the organization section; Diagnostics gains the four values and warnings.

- [ ] **Step 1: Write the failing test**

Add to `windows/tests/Elevate.App.Tests/ManagedSettingsTests.cs` a test asserting the view-model surfaces `HeaderCaption == "Managed by Contoso"` for a branded managed configuration and `null` for an unbranded one, following that file's existing view-model construction.

- [ ] **Step 2: Run to verify it fails**

Run on the Windows VM (see the `windows-vm` skill): `dotnet test windows/tests/Elevate.App.Tests/Elevate.App.Tests.csproj --nologo`
Expected: FAIL — no such property.

- [ ] **Step 3: Add the view-model property**

```csharp
    /// <summary>The organization's co-branding, or null when nothing is pushed.</summary>
    public Branding? Branding => Managed.Branding.Resolve(_managed);

    public string? HeaderCaption => Branding?.HeaderCaption;
```

- [ ] **Step 4: Update the XAML**

Replace the column-0 `TextBlock` in `PanelView.xaml` with:

```xml
            <StackPanel Grid.Column="0" Spacing="0" VerticalAlignment="Center">
                <TextBlock Text="Elevate" FontSize="16" FontWeight="SemiBold" />
                <TextBlock Text="{x:Bind ViewModel.HeaderCaption, Mode=OneWay}"
                           FontSize="11"
                           Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                           TextTrimming="CharacterEllipsis"
                           Visibility="{x:Bind ViewModel.HeaderCaption, Mode=OneWay, Converter={StaticResource NullToCollapsedConverter}}" />
            </StackPanel>
```

Reuse the project's existing null-to-visibility converter; if none exists, bind a `bool` property and use the built-in `BoolToVisibilityConverter` the project already registers. Add the first-run and Settings blocks to match Task 5.

- [ ] **Step 5: Run the tests to verify they pass**

Run on the VM: `dotnet test windows/tests/Elevate.App.Tests/Elevate.App.Tests.csproj --nologo`
Expected: PASS.

- [ ] **Step 6: Launch and screenshot on the VM**

Build, launch the tray app on the VM and screenshot the flyout branded and unbranded. Confirm the caption does not disturb the flyout height or the pinned group headers.

- [ ] **Step 7: Commit**

```bash
git add windows/src/Elevate.App windows/tests/Elevate.App.Tests/ManagedSettingsTests.cs
git commit -m "Windows: show organization co-branding in the flyout, first run and Settings"
```

---

### Task 7: CLI — `config managed`, `--version`, failure contact

**Files:**
- Modify: `cli/src/Elevate.Cli/Commands/ConfigCommands.cs`
- Modify: `cli/src/Elevate.Cli/Commands/MiscCommands.cs` (the `--version` output)
- Modify: `cli/src/Elevate.Cli/Session/ElevateSession.Managed.cs`
- Test: `cli/tests/Elevate.Cli.Tests/ManagedConfigTests.cs`

**Interfaces:**
- Consumes: `Branding.Resolve`, `Branding.SupportLine`, `Branding.HeaderCaption` (Task 4).
- Produces: nothing new for later tasks.

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/Elevate.Cli.Tests/ManagedConfigTests.cs`:

```csharp
[Fact]
public async Task ConfigManagedListsTheBrandingKeys()
{
    // `RunAsync(TestSession, params string[])` at ManagedConfigTests.cs:22 is this file's existing
    // helper; build the TestSession over a DictionaryManagedSource the way its neighbours do.
    var session = TestSession.WithManaged(new Dictionary<string, object?>
    {
        ["OrganizationName"] = "Contoso",
        ["OrganizationSupportUrl"] = "https://help.contoso.com",
    });
    var (code, output, _) = await RunAsync(session, "config", "managed");
    Assert.Equal(0, code);
    Assert.Contains("OrganizationName", output);
    Assert.Contains("Contoso", output);
    Assert.Contains("OrganizationSupportUrl", output);
}

[Fact]
public void FailuresAppendTheSupportContactWhenBranded()
{
    var branded = Branding.Resolve(ManagedConfiguration.Load(new DictionaryManagedSource(new Dictionary<string, object>
    {
        ["OrganizationName"] = "Contoso",
        ["OrganizationSupportUrl"] = "https://help.contoso.com",
    })));
    Assert.Equal("Need help? Contoso IT — https://help.contoso.com/", branded!.SupportLine);
}

[Fact]
public void FailuresAppendNothingWhenUnbranded()
{
    Assert.Null(Branding.Resolve(ManagedConfiguration.None));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test cli/tests/Elevate.Cli.Tests/Elevate.Cli.Tests.csproj --nologo`
Expected: FAIL.

- [ ] **Step 3: Implement the three surfaces**

`config managed` needs no special-casing if it already enumerates `ManagedKeys.All` — verify that it does and extend it only if it hard-codes the seven. In `--version`, after the version line:

```csharp
        if (Branding.Resolve(session.Managed) is { } branding)
        {
            console.WriteLine($"Managed by {branding.OrganizationName}");
        }
```

For failures, append the contact to the sign-in and activation error paths only:

```csharp
        if (Branding.Resolve(session.Managed)?.SupportLine is { } line)
        {
            console.Error.WriteLine(line);
        }
```

Do not add it to ordinary command output — it would corrupt piped results.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test cli/tests/Elevate.Cli.Tests/Elevate.Cli.Tests.csproj --nologo`
Expected: PASS — 164 existing plus the new ones.

- [ ] **Step 5: Commit**

```bash
git add cli/src/Elevate.Cli cli/tests/Elevate.Cli.Tests/ManagedConfigTests.cs
git commit -m "CLI: report organization branding and append the support contact to failures"
```

---

### Task 8: Enterprise kit — templates, validator and docs

**Files:**
- Modify: `docs/enterprise/keys.md`, `docs/enterprise/README.md`
- Modify: `scripts/validate-enterprise-kit.py:34` (`EXPECTED_KEYS`) and its value-rule section
- Modify: `enterprise/windows/Elevate.admx`, `enterprise/windows/en-US/Elevate.adml`
- Modify: `enterprise/windows/example.reg`, `enterprise/example/example.reg`
- Modify: `enterprise/macos/no.reothor.elevate.mobileconfig`, `enterprise/example/no.reothor.elevate.mobileconfig`
- Modify: `enterprise/macos/jamf-manifest.json`, `enterprise/macos/intune-preference-file.plist`
- Modify: `enterprise/cli/managed.json`, `enterprise/example/managed.json`

**Interfaces:**
- Consumes: the key names and value rules from Tasks 1 and 3.
- Produces: nothing for later tasks. This is the last task.

All of these must land in one commit: the validator asserts every template carries exactly the key set parsed from `keys.md`, so a partial change is a red build.

- [ ] **Step 1: Run the validator to confirm it is green before starting**

Run: `python3 scripts/validate-enterprise-kit.py`
Expected: `enterprise kit: 7 keys, all templates consistent`

- [ ] **Step 2: Add the four rows and four sections to `keys.md`**

Add to the key table, generation 4:

```markdown
| `OrganizationName` | string | Your organization's name, 1–32 characters | macOS, Windows, CLI | 4 | `Contoso` |
| `OrganizationTitleStyle` | string | `by`, `managedBy` or `none`; absent means `by` | macOS, Windows, CLI | 4 | `managedBy` |
| `OrganizationSupportUrl` | string | An `https://` URL for your help desk | macOS, Windows, CLI | 4 | `https://help.contoso.com` |
| `OrganizationSupportEmail` | string | An email address for your help desk | macOS, Windows, CLI | 4 | `it@contoso.com` |
```

Then a `##` section per key with the plist, registry and JSON syntax, matching the existing sections' shape, and stating: the name gates the other three; over-length names are rejected rather than truncated; `none` keeps About and the support contact but hides the header caption; Elevate's own name is never replaced.

- [ ] **Step 3: Update the validator**

Add the four names to `EXPECTED_KEYS`, and add value rules alongside the existing `ClientId` and `ManagedProfilesUrl` checks: the style must be one of the three names, the support URL must be `https://`, the email must contain `@`, and the name must be 1–32 characters.

- [ ] **Step 4: Update all eight templates**

Add the four keys to each, using the example values from the table. In `Elevate.admx` the style is an `enum` with three `item` elements, not a `text` box — with matching strings in `Elevate.adml`. In `jamf-manifest.json` it is a property with an option list.

- [ ] **Step 5: Update `docs/enterprise/README.md`**

The "There are seven keys" sentence becomes eleven, and its list gains the organization name, title style and support contact.

- [ ] **Step 6: Run the validator**

Run: `python3 scripts/validate-enterprise-kit.py`
Expected: `enterprise kit: 11 keys, all templates consistent`

- [ ] **Step 7: Run every suite**

```bash
cd macos && swift test && cd ..
dotnet test windows/tests/Elevate.Core.Tests/Elevate.Core.Tests.csproj --nologo
dotnet test cli/tests/Elevate.Cli.Tests/Elevate.Cli.Tests.csproj --nologo
python3 scripts/validate-enterprise-kit.py
```

Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add docs/enterprise scripts/validate-enterprise-kit.py enterprise
git commit -m "Enterprise kit: ship the four organization branding keys in every template"
```
