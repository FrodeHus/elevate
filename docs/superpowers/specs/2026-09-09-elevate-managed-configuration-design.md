# Elevate — managed configuration through Intune and Jamf

Date: 2026-09-09. Implements epic #104 (stages #101, #102, #103). Extends
`2026-09-05-elevate-signin-methods-design.md` (sign-in methods),
`2026-09-05-elevate-profiles-design.md` (profiles),
`2026-09-06-elevate-operations-design.md` (update check, diagnostics, releases)
and `2026-09-07-elevate-cli-design.md` (settings, `config`, `profiles import`).

## 1. Goal

An IT administrator rolls Elevate out to a fleet through Intune or Jamf (or
Group Policy, or a script) and pushes the company's values: the app
registration's client id, whether the update check runs, which sign-in methods
and tenants users may use, and the role-set profiles everyone should have. A
managed value wins over the user's stored value, which wins over the default.
A locked value renders disabled with a "Managed by your organization" caption.

Decisions carried over from the epic:

- **No company rebuilds.** One signed build per platform; company values travel
  as a configuration profile, an ADMX policy or `managed.json`.
- **Locked values only.** Locking is per key: an absent key leaves the choice to
  the user. There is no soft "org defaults" tier.
- **A generic signed `Elevate.pkg`** joins the DMG so Jamf and Intune can
  install silently. It carries no company values.
- **Docs and an enterprise kit** ship with the stages: `docs/enterprise/` and a
  release artifact with the templates. The epic is done when an administrator
  with no prior knowledge of Elevate can roll it out from the docs alone.

Success criteria:

- Stage 1: with `ClientId` pushed, a fresh install skips the client-id step,
  Settings shows the field disabled with the caption, writes to it are refused,
  and Diagnostics lists the managed keys in effect. With `DisableUpdateCheck`
  the daily check never runs, "Check for updates" is replaced by a caption and
  no update banner appears. The CLI reads the same values and `elevate config`
  says where each effective value came from.
- Stage 2: `AllowedSignInMethods` hides the other methods from Add account and
  from `elevate login --method`; an account added earlier with a method that is
  no longer allowed stays with a caption. `AllowedTenants` limits tenant
  discovery and manual adds; `PinnedTenants` are tracked automatically for
  every account that can reach them. Settings shows the restrictions in effect.
- Stage 3: `ManagedProfiles` (inline) and `ManagedProfilesUrl` (fetched daily,
  last copy kept on failure) publish profiles that are listed, runnable and
  bindable to the shortcut but neither editable nor deletable; roles the
  signed-in account is not eligible for plan as skipped.
- Every key works identically in the macOS app, the Windows app and the CLI,
  and is documented in `docs/enterprise/keys.md`, from which the templates in
  the enterprise kit are checked by CI.

Out of scope: a local authentication gate (#2), MSIX packaging, Intune app
configuration policies (they apply to MSIX only), per-user Group Policy
preferences beyond the HKCU policy key, signing the CLI binaries, a UI for
authoring managed profiles (a user profile's JSON is exported by the CLI).

## 2. The keys

One vocabulary, the same names on every platform. Names are case-sensitive
in plists and JSON; the registry is case-insensitive.

| Key | Type | Values | Stage |
|---|---|---|---|
| `ClientId` | string | Application (client) id of the company app registration, a GUID | 1 |
| `DisableUpdateCheck` | boolean | `true` disables the daily GitHub releases check and the update UI | 1 |
| `AllowedSignInMethods` | list of strings | Any of `ownApp`, `azureCLI`, `azurePowerShell`, `custom`; absent or empty means all | 2 |
| `AllowedTenants` | list of strings | Tenant ids or verified domains; absent or empty means no restriction | 2 |
| `PinnedTenants` | list of strings | Tenant ids or verified domains tracked for every account that can reach them | 2 |
| `ManagedProfiles` | string (JSON document) | A profile set in the format of §7.1 | 3 |
| `ManagedProfilesUrl` | string | An `https://` URL serving the same JSON, fetched once a day | 3 |

Where each platform reads them:

- **macOS app**: managed preferences for the domain `no.reothor.elevate`. The
  app reads `UserDefaults.standard.object(forKey:)` for a key only when
  `objectIsForced(forKey:)` is true; a key the user could have written in the
  same domain is ignored. Delivered by a Jamf configuration profile, an Intune
  custom profile (mobileconfig) or an Intune "Preference file" policy; for a
  local test, a plist under `/Library/Managed Preferences/<user>/`.
- **Windows app and CLI on Windows**: `HKLM\SOFTWARE\Policies\Reothor\Elevate`,
  then `HKCU\SOFTWARE\Policies\Reothor\Elevate`; per key, the machine value
  wins. Scalars are `REG_SZ` (`ClientId`, `ManagedProfilesUrl`), `REG_DWORD`
  (`DisableUpdateCheck`), `REG_MULTI_SZ` (`ManagedProfiles`, lines joined
  with newlines). Lists are subkeys holding one value per entry, named
  `1`, `2`, … (the shape ADMX `list` elements write). A `REG_MULTI_SZ` of the
  same name is accepted for a list too, for scripts.
- **CLI on macOS and Linux**: `/etc/elevate/managed.json`, a JSON object with
  the keys above. `ManagedProfiles` may be the JSON object itself or a string
  holding it. The file is trusted only when neither it nor its directory is
  writable by others; otherwise it is ignored with a warning, so a user cannot
  grant themselves a policy file that Diagnostics then reports as managed.
  (Ownership is not checked — there is no portable managed API for it, and the
  directory rule already keeps a non-root user from creating one under `/etc`.
  Deploying it root-owned 0644 stays the documented advice.)

Invalid values are ignored key by key, never the whole payload: a malformed
`ClientId` leaves the client id to the user, an unknown sign-in method name is
dropped from the list, an `http://` `ManagedProfilesUrl` is ignored. Each
such case produces a warning that Diagnostics and `elevate config managed`
show.

## 3. Core: the shared model

Both Cores (`macos/Sources/ElevateCore/Managed/`, `windows/src/Elevate.Core/Managed/`)
get the same types; the C# names follow the usual port conventions.

```swift
public enum ManagedKey: String, CaseIterable, Sendable {
    case clientId = "ClientId", disableUpdateCheck = "DisableUpdateCheck"
    case allowedSignInMethods = "AllowedSignInMethods", allowedTenants = "AllowedTenants"
    case pinnedTenants = "PinnedTenants", managedProfiles = "ManagedProfiles"
    case managedProfilesUrl = "ManagedProfilesUrl"
}

/// Where a raw managed value comes from. Each platform supplies one; tests supply a dictionary.
public protocol ManagedConfigurationSource: Sendable {
    func string(_ key: ManagedKey) -> String?
    func bool(_ key: ManagedKey) -> Bool?
    func list(_ key: ManagedKey) -> [String]?
    /// Human-readable origin for Diagnostics, e.g. "managed preferences", "HKLM policy", "/etc/elevate/managed.json".
    var origin: String { get }
}

public struct ManagedConfiguration: Hashable, Sendable {
    public var clientId: String?                 // validated GUID, lower-cased
    public var disableUpdateCheck: Bool
    public var allowedSignInMethods: Set<SignInMethodKind>?   // nil = unrestricted
    public var allowedTenants: [String]?         // ids or domains as given; nil = unrestricted
    public var pinnedTenants: [String]
    public var managedProfiles: ManagedProfileSet?
    public var managedProfilesUrl: URL?
    /// Keys that carried a usable value, for Settings and Diagnostics (names only).
    public var keysInEffect: [ManagedKey]
    /// One line per ignored or trimmed value, e.g. "AllowedSignInMethods: unknown method 'saml' ignored".
    public var warnings: [String]
    public var origin: String?
    public static let none = ManagedConfiguration()
    public var isEmpty: Bool { keysInEffect.isEmpty }
    public static func load(from source: ManagedConfigurationSource) -> ManagedConfiguration
}
```

`SignInMethodKind` already exists in C#; Swift gains
`public enum SignInMethodKind: String, CaseIterable, Sendable { case ownApp, azureCLI, azurePowerShell, custom }`
plus `SignInMethod.kind`. The allowed-methods list is parsed against these raw
values, case-insensitively.

Sources:

- Swift `ManagedPreferences(defaults: UserDefaults = .standard)`: returns a
  value only when `defaults.objectIsForced(forKey: key.rawValue)`; a list is
  accepted as `[String]` or as a single comma-separated string.
- Swift `DictionaryManagedSource([String: Any])` for tests and for the
  hosted-test screenshots.
- C# `IManagedConfigurationSource` with `RegistryManagedSource` (Windows
  only, `[SupportedOSPlatform("windows")]`, HKLM then HKCU per key),
  `JsonFileManagedSource(path)` (`/etc/elevate/managed.json`, with the
  ownership check), `DictionaryManagedSource` for tests, and
  `ManagedConfigurationSources.Default()` that picks the registry on Windows
  and the file elsewhere.

Policy helpers (pure, tested in both Cores):

```swift
public enum ManagedPolicy {
    /// nil/empty allowed set → every method; `.custom` allows any custom client id.
    public static func isAllowed(_ method: SignInMethod, by config: ManagedConfiguration) -> Bool
    /// Tenant ids allowed after domain resolution; nil → unrestricted. The home tenant of an
    /// account is exempt: it is where the account lives.
    public static func isTenantAllowed(_ tenantId: String, allowedIds: Set<String>?) -> Bool
}

/// Resolves the domain-form entries of AllowedTenants, PinnedTenants and managed profile roles
/// to tenant ids through the unauthenticated OpenID configuration endpoint (the same lookup
/// `TenantDiscovery.resolveTenantId` does), once per process, results cached in memory.
public actor ManagedTenantResolver {
    public init(http: any HTTPClient)
    public func resolve(_ entries: [String]) async -> (ids: [String], unresolved: [String])
}
```

`ManagedTenantResolver` is what the app and the CLI call at startup and after
a policy change; until it has run, domain entries are treated as unresolved
(no tenant is blocked or pinned by a name that could not be resolved, and the
unresolved names are listed as warnings).

Diagnostics: `DiagnosticsInput` gains `managed: DiagnosticsManaged?` with
`origin`, `keys: [String]` and `warnings: [String]`; the report renders a
"Managed configuration:" section after "Hot key:" — `None` when nil, else
the origin line, the key names joined on one `Keys:` line (never the values),
then the warnings. Both Cores.

## 4. Stage 1: client id and update check

### 4.1 macOS (`AppSettings`, `AppModel`, Settings, Setup)

- `AppSettings.init(defaults:managed:)` takes a `ManagedConfiguration`
  (default `ManagedConfiguration.load(from: ManagedPreferences())`). The stored
  user value moves to `storedClientId`; `clientId` becomes the resolved value:
  `managed.clientId ?? storedClientId` on read, and on write a no-op with a log
  line when `isClientIdManaged`. Every existing call site keeps reading
  `settings.clientId`. `isConfigured` therefore already honours the managed id.
- `updateCheckDisabled: Bool { managed.disableUpdateCheck }`.
  `checkForUpdates(force:)` returns at once when it is set, even when forced;
  `updateAvailable` stays nil.
- `applyClientId` throws `PIMError.unexpected(status: 0, body: "The client ID is managed by your organization")`
  when managed; Settings never calls it because the field is disabled.
- Settings, "Entra app registration": the text field is disabled and shows the
  managed value; a caption "Managed by your organization" (with a
  `building.2` glyph) replaces the "Press Return to apply" hint. The redirect
  URI row stays, it is still what an admin registers. "Updates": when
  disabled, the button is replaced by the caption "Updates are managed by your
  organization"; the banner and `updateCheckMessage` never appear.
- A new "Managed by your organization" section at the bottom of the form
  appears only when `managed.isEmpty` is false. Stage 1 lists the client id
  and the update check; stage 2 adds the lists (§6); stage 3 the profiles
  (§7). Each row is a `LabeledContent` with a plain-text value; warnings
  render underneath in orange.
- Setup: `SetupView` is shown only when `!isConfigured && identities.isEmpty`.
  With a managed client id `isConfigured` is true on a fresh install, so the
  panel goes straight to the empty accounts list, whose "Add account…" is the
  next step; no setup copy mentions Settings.
- Diagnostics passes `DiagnosticsManaged` from `settings.managed`.

### 4.2 Windows app (`AppSettings.cs`, `AppModel`, Settings window)

- `AppSettings(string? directory = null, ManagedConfiguration? managed = null)`;
  `Managed` defaults to `ManagedConfiguration.Load(ManagedConfigurationSources.Default())`.
  `ClientId` resolves as on macOS; the setter is a no-op when
  `IsClientIdManaged`. `UpdateCheckDisabled` gates `CheckForUpdatesAsync`.
- `SettingsWindow`: the client-id `TextBox` is `IsEnabled=false` with the
  managed value and a caption; the update button is replaced by the caption;
  a "Managed by your organization" group lists keys in effect.
- `PanelView` already hides the setup pane when `IsConfigured`.
- The Windows app cannot be compiled on the maintainer's Mac. Its changes are
  written against the Core and App.Model APIs and verified by the Windows CI
  job on the pull request (`dotnet build windows/Elevate.sln`).

### 4.3 CLI (`CliSettings`, `config`)

- `CliSettings(string directory, ManagedConfiguration? managed = null)`;
  `Managed` defaults from `ManagedConfigurationSources.Default()`. `ClientId`
  resolves; the setter throws `InvalidOperationException` when managed, which
  `config set client-id` turns into `CliException("client-id is managed by your organization.", ExitCodes.Usage)`.
- `elevate config` gains a `Source` column with `managed`, `user` or `default`
  per row; `--json` adds `"sources": {"clientId": "managed", …}`.
  `elevate config get <key>` prints the value as before and, unless `--quiet`,
  a stderr note `client-id: managed by your organization (HKLM policy)`.
- New `elevate config managed [--file <path>]`: prints the managed keys in
  effect, their origin and the warnings; `--file` reads that file instead of
  the platform source, as a dry run for an administrator authoring
  `managed.json`. `--json` gives `{origin, keys, warnings}`.
- The CLI's update mention after `elevate status` is skipped when disabled.

## 5. Stage 1: enterprise kit, pkg, docs

### 5.1 The kit (`enterprise/` at the repository root)

```
enterprise/
  README.md                          what is in here, one paragraph per file
  keys.md -> ../docs/enterprise/keys.md is the source; the kit copies it at release time
  windows/Elevate.admx
  windows/en-US/Elevate.adml
  windows/example.reg                the worked example as a .reg file (HKLM)
  macos/no.reothor.elevate.mobileconfig       template with placeholders
  macos/intune-preference-file.plist          the same keys as a plain plist for Intune "Preference file"
  macos/jamf-manifest.json                    Jamf Pro application & custom settings manifest
  cli/managed.json                   template
  example/                           the worked example (§5.3): mobileconfig, managed.json, profiles.json, example.reg
```

ADMX shape: namespace `Reothor.Elevate`, category "Elevate" under
"Reothor", class `Both` (the same policies under HKLM and HKCU), registry key
`Software\Policies\Reothor\Elevate`. Policies: `ClientId` (text element,
`required`), `DisableUpdateCheck` (enabled → `REG_DWORD 1`, disabled → `0`),
`AllowedSignInMethods`, `AllowedTenants`, `PinnedTenants` (`list` elements
writing under the subkey of the same name, `additive="false"`),
`ManagedProfilesUrl` (text), `ManagedProfiles` (`multiText`). Every policy
has `displayName`, `explainText` and a presentation in the ADML; the
`supportedOn` is a product definition "Elevate 1.6 or later".

The mobileconfig template is a `com.apple.ManagedClient.preferences` payload
for `no.reothor.elevate` with every key present in a `Forced` array item,
placeholders such as `00000000-0000-0000-0000-000000000000` and comments
telling the author which keys to delete. `PayloadIdentifier`, `PayloadUUID`
and the display names are set so the file imports into Jamf and Intune.

Validation (`scripts/validate-enterprise-kit.py`, run by a new
`.github/workflows/enterprise-kit.yml` on changes under `enterprise/`,
`docs/enterprise/` or the script): the ADMX and ADML are well-formed XML, each
ADMX policy's `displayName`, `explainText` and `presentation` reference
resolves in the ADML, the set of policy names equals the key table in
`docs/enterprise/keys.md`, every key in the table appears in the
mobileconfig, the Intune plist, the Jamf manifest and `cli/managed.json`, the
plists parse (`plistlib`), the JSON files parse, and the example profile set
validates against §7.1 (a small Python re-implementation of the shape
check). The script exits non-zero with one line per failure.

### 5.2 Release changes (`.github/workflows/release.yml`, `docs/releasing.md`)

- macOS job: after "Package DMG", a "Package pkg" step runs
  `pkgbuild --component Elevate.app --install-location /Applications --identifier no.reothor.elevate --version "$VERSION"`
  into `dist/Elevate-$VERSION.pkg`. When the new optional secrets
  `MACOS_INSTALLER_CERT_P12` and `MACOS_INSTALLER_CERT_PASSWORD` exist (a
  Developer ID Installer certificate), the pkg is signed with
  `productsign` and notarized and stapled like the DMG; otherwise it is
  uploaded unsigned and the release notes say so. The checksum is written
  after stapling. The artifact glob and the `gh release create` list include
  `Elevate-$VERSION.pkg` and its `.sha256`.
- publish job: a "Enterprise kit" step copies `enterprise/` and
  `docs/enterprise/keys.md` into `Elevate-enterprise-kit-$VERSION/`, stamps
  the version into the kit's README, zips it as
  `dist/Elevate-enterprise-kit-$VERSION.zip` with a `.sha256`, and adds both to
  the release. The release notes get an "Enterprise" paragraph naming the
  pkg, the kit and `docs/enterprise/README.md`.
- `docs/releasing.md`: the two new secrets, what the pkg step does with and
  without them, and the kit.

### 5.3 Documentation (`docs/enterprise/`)

Written for an administrator who has never seen Elevate. Each page starts
with what the reader ends up with and lists prerequisites.

1. `README.md`: the model (managed → user → default), locked values only,
   what the kit contains, a table of which how-to to read per platform and
   MDM, and the verification step every how-to ends with (Settings shows the
   caption, Diagnostics lists the keys, `elevate config`).
2. `keys.md`: the reference table of every key with type, allowed values,
   platform availability, stage, and an example in each syntax (plist, registry,
   JSON). This is the single source of truth the validation script reads:
   the table rows are parsed, so the format is fixed (`| \`Key\` | type | … |`).
3. `macos-jamf.md`: upload the pkg, build the configuration profile from the
   template (or Jamf's application & custom settings with the manifest), scope,
   verify.
4. `macos-intune.md`: the pkg as a macOS app, the custom profile from the
   mobileconfig or the Preference file with the plist, verify.
5. `windows-intune.md`: the MSI as a Win32 app (`msiexec /i … /qn`, detection
   by the per-user install path), import the ADMX/ADML, set the policies in
   the Settings catalog, verify.
6. `windows-group-policy.md`: central store placement, the same policies
   through a GPO, `gpupdate`, verify with `reg query`.
7. `cli.md`: `managed.json` on macOS and Linux (ownership rules, an Ansible
   task and a Jamf script), the registry keys on Windows, `elevate config`,
   `elevate config managed --file` as a dry run.
8. `profiles.md` (stage 3): author a profile set (the schema with an example
   of each role kind, how the CLI exports an existing user profile as a
   starting point), publish it inline or through `ManagedProfilesUrl`, update
   it, what users see, why a role shows as skipped.
9. `troubleshooting.md`: how a user or admin tells a value is managed, why a
   field is disabled, why a tenant or sign-in method is missing, why a profile
   cannot be edited, what Diagnostics and `elevate config managed` report,
   and the warnings and what causes each.

Cross-links: `docs/README.md` gets an "Enterprise" list under Reference and
the spec under Design documents; the top-level `README.md` gets the
"Enterprise-ready" section between *Install* and *Repository layout*, an
entry in *Documentation*, `enterprise/` in *Repository layout*, and nothing
in *Roadmap* (the epic ships with this branch). `macos/README.md`,
`windows/README.md` and `cli/README.md` each get a short "Managed
configuration" pointer. `docs/troubleshooting.md` (user guide) gets a
"Managed by your organization" entry.

## 6. Stage 2: sign-in methods and tenants

### 6.1 Sign-in methods

- macOS `availableMethods` filters `SignInMethod.builtIn` through
  `ManagedPolicy.isAllowed`; `isAvailable(_:)` returns false for a disallowed
  method (including `.custom` when `custom` is not allowed), so
  `addAccount(method:)` and `retrySignIn` already refuse, with the notice
  "That sign-in method is not permitted by your organization".
  `AddAccountView` hides the Custom row when custom is not allowed and, when
  only one method remains, still shows the radio group (one row) so the
  limitation text stays visible.
- The account row in the panel (`IdentitySection`) shows the caption
  "Sign-in method no longer permitted by your organization" under an account
  whose method is disallowed; "Sign in" on it is disabled with the same text.
- Windows: `AvailableMethods`, `IsAvailable`, `AddAccountWindow` (hide radio
  rows), account rows: the same.
- CLI: `AccountCommands.ParseMethod` throws
  `CliException("The sign-in method '<name>' is not permitted by your organization. Allowed: own, cli.", ExitCodes.Usage)`;
  `elevate accounts` marks a disallowed account with `not permitted` in the
  Flags column; `elevate login` without `--method` picks the first allowed
  method rather than `own` when `own` is disallowed.

### 6.2 Allowed tenants

- At bootstrap (and when the model is rebuilt after a client-id change) the
  app resolves `allowedTenants` and `pinnedTenants` through
  `ManagedTenantResolver`, then keeps `allowedTenantIds: Set<String>?` and
  `pinnedTenantIds: [String]` on the model (`nil` until resolved when the
  entries include unresolved domains: the restriction is not applied until
  the names are known, and a warning says so).
- `addTenant(identityId:domainOrId:)` resolves the id first and then throws
  `PIMError.unexpected(status: 0, body: "Tenant <name> is not permitted by your organization")`
  when it is not allowed. `discoverTenants` returns the found list unchanged;
  `trackTenants` skips disallowed tenants, and `DiscoverTenantsView` shows a
  disallowed tenant greyed out with the caption "Not permitted by your
  organization" rather than hiding it, so the user knows why it is missing.
- On load, tracked tenants that are neither home nor allowed are removed from
  the state (with a log line naming them), so a policy arriving after the fact
  cleans up.
- The CLI mirrors the same in `AddTenantAsync`, `TrackTenants` and at load,
  and `tenants discover` marks disallowed rows in a Flags column.

### 6.3 Pinned tenants

- After a successful `addAccount` and at bootstrap for each identity, every
  pinned tenant id not yet tracked for that identity is upserted as a
  `TenantContext(source: .discovered)` with the display name from Graph
  `/organization` (fallback: the entry as given), then refreshed. A pinned
  tenant the account cannot reach ends up with the usual discovery error;
  it stays listed, since the policy asked for it.
- `isPinnedTenant(_ key: TenantKey) -> Bool` is derived from
  `pinnedTenantIds`; the source stays `.discovered` so older builds still
  decode the state file. The tenant menu hides "Remove tenant" for a pinned
  tenant and shows "Pinned by your organization" instead; `removeTenant`
  refuses with a notice. CLI: `tenants remove` errors with the same text;
  `tenants` shows `pinned` in Flags.

### 6.4 Settings

The "Managed by your organization" section lists "Allowed sign-in methods"
(display names), "Allowed tenants" and "Pinned tenants" (the entries as given,
with the resolved id when it differs), each only when the key is in effect.

## 7. Stage 3: managed profiles

### 7.1 The profile set format

User profiles bind roles to account ids, which an administrator cannot know.
A managed profile therefore names roles by tenant and role, and is resolved
against the signed-in accounts at runtime.

```json
{
  "version": 1,
  "profiles": [
    {
      "id": "prod-incident",
      "name": "Prod incident",
      "reason": "Incident response",
      "pinned": true,
      "roles": [
        { "kind": "azureResource", "tenant": "contoso.com",
          "scope": "/subscriptions/00000000-0000-0000-0000-000000000001",
          "role": "Contributor", "duration": "PT2H" },
        { "kind": "entraDirectory", "tenant": "contoso.com",
          "role": "Security Reader" },
        { "kind": "group", "tenant": "contoso.com",
          "group": "SRE on-call", "access": "member", "duration": "PT4H" }
      ]
    }
  ]
}
```

- `version` is `1`; a higher version is rejected with a warning.
- `id` is a stable slug (`[a-z0-9-]{1,64}`), unique in the set; the
  `ActivationProfile.id` is the UUID v5 of `managed-profile:<slug>` under
  the namespace `6ba7b810-9dad-11d1-80b4-00c04fd430c8` (RFC 4122 DNS
  namespace), so the same profile has the same id on every machine, and a
  hot-key binding survives a reinstall.
- `name` is required; `reason` (optional) prefills the run sheet's
  justification; `pinned` (optional, default false) shows the profile as a
  chip in the panel. Managed chips come first and do not count against
  `ProfilePins.limit`.
- `roles[]`: `kind` is `entraDirectory`, `azureResource` or `group`;
  `tenant` is a tenant id or a verified domain; `duration` (optional) is an
  ISO 8601 duration used as the entry's proposed duration, capped by the
  policy as usual. Per kind: `role` (Entra: display name or role template id;
  Azure: display name or role definition GUID) and `scope` (Azure only, the
  ARM scope, compared case-insensitively; Entra `directoryScope` defaults to
  `/`); `group` (display name or object id) and `access` (`member`, default,
  or `owner`).
- `ManagedProfileSet.parse(data:) throws -> ManagedProfileSet` reports every
  shape error with the profile id and field.

Inline and URL: the inline `ManagedProfiles` document and the fetched one are
merged by `id`, the fetched entry winning. The fetch (`ManagedProfileFetcher`
in Core: `https` only, 1 MB cap, `Accept: application/json`) runs at startup
when the cached copy is older than 24 hours, and once a day after; the last
successful body is cached as `managed-profiles.json` next to `state.json`
and used when the fetch fails (the failure is logged and listed as a warning).

### 7.2 Resolution (pure, both Cores, tested)

```swift
public enum ProfileSource: String, Codable, Sendable { case user, managed }
// ActivationProfile gains `public var source: ProfileSource = .user`, encoded only when managed;
// managed profiles are never written to state.json.

public struct ManagedProfileResolution: Sendable {
    public var profiles: [ActivationProfile]     // source == .managed, ids per §7.1
    public var warnings: [String]                 // "Prod incident: no account in tenant fabrikam.com"
}
public enum ManagedProfileResolver {
    public static func resolve(_ set: ManagedProfileSet, tenantIds: [String: String] /* entry → id */,
                               tenants: [TenantContext], roles: [RoleKey: EligibleRole]) -> ManagedProfileResolution
}
```

For each role spec: the tenant id comes from `tenantIds` (domains resolved by
`ManagedTenantResolver`; an unresolved domain drops the role with a warning);
for every `TenantContext` with that tenant id, every `EligibleRole` in it
matching the spec (kind, then name or id case-insensitively, plus scope /
directory scope / access) yields an entry; when a tenant is tracked but no
role matches, one placeholder entry is added with a `RoleKey` built from the
spec (scope ids as given, the name in the id field when only a name was
given) so the planner marks it `.notEligible` once the tenant is loaded and
`.notLoaded` before. When no account tracks the tenant at all the role is
dropped and a warning names the profile and tenant. Entries are ordered as
`saveProfile` orders them.

### 7.3 App and CLI behaviour

- macOS `AppModel.managedProfiles: [ActivationProfile]` is recomputed from
  `settings.managed`, the fetched set, the resolved tenant ids, `state.tenants`
  and `roles` whenever those change (a computed property over observable
  state). `profiles` becomes `state.profiles + managedProfiles`;
  `profile(id:)` searches both; `pinnedProfiles` puts pinned managed profiles
  first. `plan(for:)` and `runProfile` use `profile(id:)`; `runProfile` skips
  the "remember on the profile" step for a managed profile (per-role memory
  is still updated by `activate`). `renameProfile`, `deleteProfile`,
  `updateProfile`, `addProfileEntries`, `removeProfileEntry`,
  `setProfileEntryDuration`, `setPinned` and `moveProfile` ignore managed ids.
- `ManageProfilesView`: a managed row shows a `building.2` marker and the
  editor shows the name read-only, the caption "Published by your
  organization", Run… and the shortcut toggle, no pin toggle, no Add roles…,
  no minus buttons, no Delete…. The run sheet is unchanged: placeholder
  entries render as "not eligible · skipped".
- Windows: the same in `AppModel.Profiles` and `ManageProfilesWindow`.
- CLI: `ElevateSession.Profiles` = user + managed; `profiles list` gets a
  Source column (`user` / `managed`); `profiles show` prints the source;
  `rename`, `delete` and `save` onto a managed name error with
  "'<name>' is published by your organization and cannot be changed."
  `profiles run` and the hot-key path are unchanged. New
  `elevate profiles export <name-or-id>` prints one user profile in the §7.1
  shape (names looked up from the loaded roles, tenant ids as ids) so a team
  can start a managed set from what someone already built.
- Settings' managed section lists "Managed profiles: N (inline)",
  "Managed profiles URL: <url> · fetched <relative time>" and the resolution
  warnings. Diagnostics adds the managed profile names under "Profiles:" with
  a `(managed)` suffix.

## 8. Testing

Swift (`ElevateCoreTests`, `ElevateAppTests`) and C# (`Elevate.Core.Tests`,
`Elevate.Cli.Tests`), all through dictionary sources; no test touches real
managed preferences, the registry or `/etc`:

- Parsing: each key from each accepted shape; invalid values ignored with the
  expected warning; keysInEffect; the registry list shapes (subkey values,
  REG_MULTI_SZ) through a fake registry abstraction; the managed.json
  ownership check through an injected `Func<string, bool>`.
- Policy helpers, `ManagedTenantResolver` (stub HTTP), `ManagedProfileSet`
  parsing errors, `ManagedProfileResolver` (matches by name and by id, scope
  case, placeholder entries, dropped tenant, ordering, stable UUID).
- App model (Swift, and the CLI session in C#): managed client id makes
  `isConfigured` true and `applyClientId` throw; update check skipped when
  disabled; disallowed method refused in add and retry and hidden from
  `availableMethods`; `trackTenants` skips disallowed, `addTenant` throws,
  load removes disallowed tenants; pinned tenants tracked after `addAccount`
  and at bootstrap, `removeTenant` refused; managed profiles listed, planned,
  run (state untouched), not deletable; merge of inline and fetched sets;
  fetch failure keeps the cached copy. Diagnostics renders the section.
- CLI commands: `config` sources, `config set client-id` refused,
  `config managed --file`, `login --method` refused, `tenants remove` refused,
  `profiles list` source column, `profiles export` round-trips through
  `ManagedProfileSet.parse`.
- Kit: `scripts/validate-enterprise-kit.py` runs in CI and locally
  (`python3 scripts/validate-enterprise-kit.py`).

## 9. Delivery

One branch, `managed-configuration`, one pull request closing #101, #102,
#103 and #104, with the Windows app changes verified by the Windows CI job
and listed in the PR as untested on a device. The changelog gets an
"Unreleased" section naming the three stages, the pkg and the kit.
