# Elevate — enterprise co-branding

Date: 2026-09-18. Extends `2026-09-09-elevate-managed-configuration-design.md`
(the seven managed keys, their sources, locking and warning model).

## 1. Goal

An organization deploys Elevate to its fleet and wants it to read as something
their IT department provides rather than a tool each user found. They push an
organization name and a support contact through the managed configuration they
already use, and the app shows "by Contoso" beside its own name plus a route to
their help desk.

Decisions taken during design, and the reasoning that is easy to lose later:

- **Co-brand, never white-label.** "Elevate" stays the primary mark in the
  header, on first run and in About. The organization's name is *added*, never
  substituted. This is what Chrome Enterprise (`EnterpriseCustomLabel`), Intune
  Company Portal, Entra company branding and Nextcloud CE all do. It keeps the
  MIT licence and the trademark story clean, and it means a user who files a bug
  still knows whose bug tracker to use — which matters because the build is
  signed by this project and checks this project's update feed.
- **No logo in this iteration.** Deliberately dropped. A logo means fetching or
  decoding untrusted image bytes, a cache, a size cap and a new failure mode, to
  decorate a 380pt popover. Text carries the same signal at a fraction of the
  risk. The key names leave room for a logo key later.
- **The support contact is the feature.** Across the prior art the logo is
  decoration and the "contact IT" block is the functional part. It is what a
  user reaches for when activation fails, so it appears where failures surface.
- **Managed-only.** Unlike the existing seven keys these have no user tier, no
  default and no stored value, so they are not "locked" in the existing sense
  and render no "Managed by your organization" caption. They are pushed or they
  are absent.

Success criteria:

- An administrator who has deployed the existing keys can add branding from
  `keys.md` alone, in the format they already use.
- An unbranded install renders identically to today, on all three platforms.
- A malformed branding value costs that one key and produces one warning; it
  never degrades the rest of the configuration.

## 2. The keys

Four keys, generation 4, carried by every template the enterprise kit ships.

| Key | Type | Accepted | Rejected |
|---|---|---|---|
| `OrganizationName` | string | 1–32 characters after trimming | empty, whitespace-only, or over 32 |
| `OrganizationTitleStyle` | string | `by`, `managedBy`, `none` | any other value |
| `OrganizationSupportUrl` | string | an `https://` URL | `http://`, other schemes, unparseable |
| `OrganizationSupportEmail` | string | contains `@`, no whitespace | otherwise |

Rules:

- **`OrganizationName` gates the family.** Without it the other three are inert,
  each producing a warning. "by ⟨nothing⟩" and an unattributed "Get help" link
  are both worse than no branding.
- **`OrganizationTitleStyle` defaults to `by`** when the name is present and the
  style is absent.
- **`none`** suppresses the header line but keeps About, Diagnostics and the CLI
  support text. It exists for organizations that want the support route without
  the everyday visual.
- **Per-key rejection**, exactly as the existing keys: a bad value appends to
  `warnings`, is left out of `keysInEffect`, and never fails the load.
- **An over-length name is rejected, not truncated**, with the actual length in
  the warning, so the administrator finds out rather than the user.

### 2.1 Why 32 characters

The macOS panel is 380pt wide (`PanelMetrics.width`), and the header row already
spends roughly 160pt on the offline pill, the search, select and refresh toggles
and Settings. An inline suffix on the title line has about 26 characters to work
with, which `Elevate — Managed by ⟨name⟩` exceeds for almost any real name. §3
puts the branding on its own line instead, where 32 characters fits both
phrasings at caption size on both platforms with margin to spare.

## 3. Rendering

### 3.1 Header, both apps

The product name stays exactly as it is — `Text("Elevate")` in
`macos/Sources/ElevateApp/Views/PanelView.swift` and the matching `TextBlock` in
`windows/src/Elevate.App/Views/PanelView.xaml`. The organization line goes
directly beneath it as a secondary caption, inside the existing first column:

```
Elevate                    ⌕ ☑ ⟳ ⚙
by Contoso
```

Chosen over an inline `Elevate by Contoso` suffix because it does not truncate
at realistic name lengths, needs no font-size juggling, keeps the product mark
at full weight, and costs vertical space only when branding is pushed. When
branding is absent the header is byte-identical to today.

The caption is `by Contoso` for `by` and `Managed by Contoso` for `managedBy`;
it is not rendered for `none` or when unbranded.

### 3.2 First run

One line under "Complete initial setup" in `SetupView.swift` and its XAML
counterpart: `Provided by Contoso.`, followed by the support link when set.
This is the highest-value placement — it is where the user forms their idea of
whose software this is.

### 3.3 Settings / About

A section headed with the organization name, containing the support URL as a
link and the email as a `mailto:`. Rendered only when `OrganizationName` is
present, including under `none`.

### 3.4 Diagnostics

The four resolved values and any branding warnings, alongside the existing
managed-key reporting.

### 3.5 CLI

The CLI shares `Elevate.Core` with the Windows app, so it gets the same resolved
`Branding`. It surfaces it in three places and deliberately nowhere else:

- `elevate config managed` lists the four keys like the other seven.
- `elevate --version` gains a `Managed by Contoso` line.
- **Sign-in and activation failures append the support contact**, e.g.
  `Need help? Contoso IT — https://help.contoso.com`.

No banner on ordinary commands: it would be noise on every invocation and would
corrupt piped output. The failure path is where a CLI user is stuck and needs to
know who to call.

## 4. Components

Swift `ElevateCore/Managed` is the source of truth; C# `Elevate.Core/Managed` is
its documented port and serves both the Windows app and the CLI. Every change
lands in both, in the same shape.

```
mobileconfig / registry / managed.json
        │
        ▼
ManagedConfigurationSource                (unchanged — four more reads)
        │
        ▼
ManagedConfiguration.load(from:)          + 4 fields, validation, warnings
        │
        ▼
Branding.resolve(from:) -> Branding?      new, one file per core
        │   nil when unbranded
        │   .headerCaption   "by Contoso" | "Managed by Contoso" | nil
        │   .firstRunLine    "Provided by Contoso."
        │   .supportUrl / .supportEmail / .hasSupport
        ▼
  ┌─────┴──────────────┬───────────────────────┐
Panel header      Setup / About        CLI failures, --version,
(mac + win)       (mac + win)          config managed
```

`Branding` is a separate type rather than four fields read at each call site:
four surfaces × three platforms is twelve places that would otherwise re-derive
the phrasing. One resolve function per language keeps the style rules testable
in isolation, and the `nil`-when-unbranded shape makes "unchanged when
unbranded" a single assertion instead of a visual diff.

**No new I/O.** No fetch, no cache, no file read, no image decode. The only
untrusted input is four short strings, each length- and format-checked.

## 5. Artifacts

`scripts/validate-enterprise-kit.py` parses the key table from
`docs/enterprise/keys.md` and asserts every template in `enterprise/` carries
exactly that set, against a hardcoded `EXPECTED_KEYS`. Adding four keys fails CI
until all of these are updated together:

| File | Change |
|---|---|
| `docs/enterprise/keys.md` | 4 table rows (generation 4) + 4 sections with plist, registry and JSON syntax |
| `scripts/validate-enterprise-kit.py` | `EXPECTED_KEYS`, plus value rules for the enum, the https URL and the email |
| `enterprise/windows/Elevate.admx`, `en-US/Elevate.adml` | 4 policies; the style as an `enum`, not free text |
| `enterprise/windows/example.reg`, `enterprise/example/example.reg` | example values |
| `enterprise/macos/no.reothor.elevate.mobileconfig` and the `example/` copy | 4 payload keys |
| `enterprise/macos/jamf-manifest.json` | 4 entries, the style as an option list |
| `enterprise/macos/intune-preference-file.plist` | 4 keys |
| `enterprise/cli/managed.json`, `enterprise/example/managed.json` | 4 keys |
| `docs/enterprise/README.md` | "seven keys" becomes eleven |

## 6. Testing

Following the existing test files rather than introducing a layout:

- **Core, both languages** — `ManagedConfigurationTests`, `AppModelManagedTests`:
  the full validation matrix, each invalid form, name-without-style defaulting
  to `by`, style and support keys orphaned without a name, and the length
  boundary asserted at exactly 32 and 33.
- **`Branding.resolve`** — its own test file per core: the three styles, `nil`
  when unbranded, support present and absent independently of style.
- **CLI** — `ManagedConfigTests` for `config managed` output; a test that a
  sign-in failure with branding appends the contact line and without branding
  does not.
- **Windows app** — `ManagedSettingsTests` for header and About rendering.
- **Regression guard** — one test per platform asserting that an unbranded
  configuration yields no header caption and no About section. This protects the
  overwhelming majority of users, who will never push these keys.

## 7. Risks

- **Header height.** The header gains a conditional second line, shifting the
  panel's vertical layout when branding is present. Both panels are scroll
  containers with pinned section headers, so this must be checked against the
  popover height logic on both platforms — a verification step, not an
  anticipated code change.
- **Windows and CLI verification on macOS.** The Windows app cannot be built or
  screenshotted from the Mac; that work runs on the Parallels VM.

## 8. Not doing

No logo, image or icon. No colour or theme control. No app-name replacement. No
per-tenant branding. No user-settable tier. Nothing in notifications or the tray
tooltip. No change to the update feed or its visibility.
