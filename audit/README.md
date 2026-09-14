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
25 findings: 13 high, 5 medium, 5 low, 2 info.
```

## Build and test

```sh
dotnet test audit/Elevate.Audit.sln
./audit/package.sh publish 0.0.0 osx-arm64      # one self-contained binary in audit/dist/osx-arm64/
./audit/dist/osx-arm64/elevate-audit --from-snapshot audit/tests/Elevate.Audit.Tests/Fixtures/snapshots/sample.json
```

Golden files (`audit/tests/Elevate.Audit.Tests/Golden/`, `Fixtures/snapshots/sample.json`,
`site/audit-sample.html`) are regenerated with `ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln`.

Notable changes go in [CHANGELOG.md](CHANGELOG.md) under `## [Unreleased]`. The tool is versioned
and released separately from the app, under `audit-v<x.y.z>` tags; see
[docs/releasing.md](../docs/releasing.md#releasing-the-audit-tool).

## Layout

`src/Elevate.Audit` — `Auth` (two MSAL public clients, in memory), `Collectors` (Graph and ARM
reads into an immutable `Snapshot`), `Rules` (pure `Snapshot → Finding` rules), `Rendering`
(terminal, JSON, HTML), `Commands`. `tests/Elevate.Audit.Tests` — stub-HTTP collector tests,
builder-based rule tests, golden renderer tests. References `windows/src/Elevate.Core` for the
Graph transport, JSON options and the Entra role catalogue.
