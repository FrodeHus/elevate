# Changelog

All notable changes to `elevate-audit` are documented here. The tool is versioned and released
separately from the Elevate app and CLI: its tags are `audit-v<x.y.z>` and its releases are
titled "Elevate Audit x.y.z". Its own numbering starts at 1.0.0; before that it shipped inside the
app releases up to Elevate 1.6.7, and its notes lived in the root [CHANGELOG.md](../CHANGELOG.md).

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- Windows: `elevate-audit.exe` is code-signed with the project's Certum certificate (publisher
  "Open Source Developer Frode Hus"), like the app and the CLI.

## [1.0.0] - 2026-09-14

### Changed

- Released on its own: `elevate-audit` now has its own version number, tag (`audit-v<x.y.z>`),
  GitHub Release ("Elevate Audit x.y.z") and changelog, so a change to the audit tool no longer
  rebuilds and re-signs the apps and the CLI, and an app release no longer republishes the audit
  tool. The download URLs move to the `audit-v` release; the Homebrew formula and the winget
  manifest follow.
- `elevate-audit update` looks for `audit-v` releases only, so the app releases that used to carry
  an audit archive are never offered as an upgrade.

## Shipped with Elevate [1.6.7] - 2026-09-14

The last audit build inside an app release, before the tool's own numbering began at 1.0.0.

### Changed

- The HTML report opens with an executive summary — a one-sentence verdict and one tile per area
  (Entra roles, PIM for Groups, Azure RBAC, Guests, Workload identities, Hygiene, Coverage) that
  links to its section — instead of four severity counts. Sections are collapsible, group
  findings roll up into one card per group with a membership outline, a nesting diagram and the
  people reached, and a small inline script adds search, severity filters, expand/collapse and
  50-row caps. The page is complete with JavaScript off and still loads nothing from the network.
- Every "Start here" item now carries a pill naming its area, so "User Access Administrator on
  Production" reads as Azure RBAC rather than an Entra role, and the pill links to that section.

[Unreleased]: https://github.com/FrodeHus/elevate/compare/audit-v1.0.0...HEAD
[1.0.0]: https://github.com/FrodeHus/elevate/compare/v1.6.7...audit-v1.0.0
[1.6.7]: https://github.com/FrodeHus/elevate/releases/tag/v1.6.7
