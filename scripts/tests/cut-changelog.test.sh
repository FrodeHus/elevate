#!/usr/bin/env bash
# Tests for scripts/cut-changelog.sh: the default root changelog, and a second changelog with its
# own tag prefix (the audit tool's). Run: scripts/tests/cut-changelog.test.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="$ROOT/scripts/cut-changelog.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

write_changelog() {
  mkdir -p "$(dirname "$1")"
  cat > "$1" <<'MD'
# Changelog

## [Unreleased]

### Fixed

- Something.

## [1.6.7] - 2026-09-14

### Changed

- Earlier.

[Unreleased]: https://github.com/FrodeHus/elevate/compare/v1.6.7...HEAD
[1.6.7]: https://github.com/FrodeHus/elevate/compare/v1.6.6...v1.6.7
MD
}

section() { awk -v h="## [$2]" '/^## \[/{f = (index($0, h) == 1); next} f' "$1"; }

# A copy of the script in a throwaway repo layout, so the real CHANGELOG.md is never touched.
mkdir -p "$TMP/repo/scripts" && cp "$SCRIPT" "$TMP/repo/scripts/"
CUT="$TMP/repo/scripts/cut-changelog.sh"

# Default: CHANGELOG.md at the repo root, v-prefixed tags.
write_changelog "$TMP/repo/CHANGELOG.md"
"$CUT" 1.6.8 2026-09-15 FrodeHus/elevate >/dev/null
grep -q '^## \[1.6.8\] - 2026-09-15$' "$TMP/repo/CHANGELOG.md" || fail "root: no 1.6.8 section"
grep -q '^\[Unreleased\]: https://github.com/FrodeHus/elevate/compare/v1.6.8...HEAD$' "$TMP/repo/CHANGELOG.md" || fail "root: Unreleased link"
grep -q '^\[1.6.8\]: https://github.com/FrodeHus/elevate/compare/v1.6.7...v1.6.8$' "$TMP/repo/CHANGELOG.md" || fail "root: 1.6.8 link"
section "$TMP/repo/CHANGELOG.md" Unreleased | grep -q '[^[:space:]]' && fail "root: Unreleased not emptied"
section "$TMP/repo/CHANGELOG.md" 1.6.8 | grep -q '^- Something.$' || fail "root: entry not moved"

# --file and --tag-prefix: the audit changelog, whose tags are audit-v<x.y.z>. The first cut after
# the split compares against the plain v tag its Unreleased link already names, not audit-v1.6.7,
# which never existed.
write_changelog "$TMP/repo/audit/CHANGELOG.md"
"$CUT" --file audit/CHANGELOG.md --tag-prefix audit-v 1.6.8 2026-09-15 FrodeHus/elevate >/dev/null
grep -q '^## \[1.6.8\] - 2026-09-15$' "$TMP/repo/audit/CHANGELOG.md" || fail "audit: no 1.6.8 section"
grep -q '^\[Unreleased\]: https://github.com/FrodeHus/elevate/compare/audit-v1.6.8...HEAD$' "$TMP/repo/audit/CHANGELOG.md" || fail "audit: Unreleased link"
grep -q '^\[1.6.8\]: https://github.com/FrodeHus/elevate/compare/v1.6.7...audit-v1.6.8$' "$TMP/repo/audit/CHANGELOG.md" || fail "audit: 1.6.8 link"
grep -q '^\[1.6.7\]: https://github.com/FrodeHus/elevate/compare/v1.6.6...v1.6.7$' "$TMP/repo/audit/CHANGELOG.md" || fail "audit: old link changed"
grep -q 'audit-v' "$TMP/repo/CHANGELOG.md" && fail "audit: root changelog touched"

# A second cut chains from the prefixed tag.
perl -0pi -e 's/^## \[Unreleased\]\n/## [Unreleased]\n\n- More.\n/m' "$TMP/repo/audit/CHANGELOG.md"
grep -q '^- More.$' "$TMP/repo/audit/CHANGELOG.md" || fail "test setup: could not add an entry"
"$CUT" --file audit/CHANGELOG.md --tag-prefix audit-v 1.6.9 2026-09-16 FrodeHus/elevate >/dev/null
grep -q '^\[1.6.9\]: https://github.com/FrodeHus/elevate/compare/audit-v1.6.8...audit-v1.6.9$' "$TMP/repo/audit/CHANGELOG.md" || fail "audit: second cut link"
grep -q '^\[Unreleased\]: https://github.com/FrodeHus/elevate/compare/audit-v1.6.9...HEAD$' "$TMP/repo/audit/CHANGELOG.md" || fail "audit: second Unreleased link"

# Refusals: an empty Unreleased section, a version that already has a section, a missing file.
if "$CUT" --file audit/CHANGELOG.md --tag-prefix audit-v 1.7.0 2026-09-17 >/dev/null 2>&1; then fail "empty Unreleased accepted"; fi
write_changelog "$TMP/repo/CHANGELOG.md"
if "$CUT" 1.6.7 >/dev/null 2>&1; then fail "duplicate version accepted"; fi
if "$CUT" --file nope/CHANGELOG.md 1.6.8 >/dev/null 2>&1; then fail "missing file accepted"; fi

echo "cut-changelog: all tests passed"
