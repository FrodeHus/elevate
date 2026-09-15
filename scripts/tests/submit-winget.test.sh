#!/usr/bin/env bash
# Tests for scripts/submit-winget.sh: the winget-pkgs path rule (every dot-separated segment of
# the identifier is a directory level), the pull request title, and the no-token exit.
# Run: scripts/tests/submit-winget.test.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="$ROOT/scripts/submit-winget.sh"
fail() { echo "FAIL: $*" >&2; exit 1; }

# shellcheck source=../submit-winget.sh
source "$SCRIPT"

[ "$(manifest_path Reothor.Elevate 1.7.0)" = "manifests/r/Reothor/Elevate/1.7.0" ] || fail "app path"
[ "$(manifest_path Reothor.Elevate.CLI 1.7.0)" = "manifests/r/Reothor/Elevate/CLI/1.7.0" ] || fail "CLI path is nested"
[ "$(manifest_path Reothor.Elevate.Audit 1.0.1)" = "manifests/r/Reothor/Elevate/Audit/1.0.1" ] || fail "audit path is nested"
[ "$(manifest_path Microsoft.PowerToys 0.80.0)" = "manifests/m/Microsoft/PowerToys/0.80.0" ] || fail "partition is the lower-cased first letter"

[ "$(pr_title Reothor.Elevate 1.7.0 no)" = "New package: Reothor.Elevate version 1.7.0" ] || fail "new package title"
[ "$(pr_title Reothor.Elevate 1.7.1 yes)" = "New version: Reothor.Elevate version 1.7.1" ] || fail "new version title"

pr_body Reothor.Elevate.CLI 1.7.0 v1.7.0 FrodeHus/elevate | grep -q 'releases/tag/v1.7.0' || fail "body links the release tag"

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
for f in Reothor.Elevate.yaml Reothor.Elevate.installer.yaml Reothor.Elevate.locale.en-US.yaml; do echo "x: 1" > "$TMP/$f"; done
out="$(WINGET_TOKEN= "$SCRIPT" "$TMP" Reothor.Elevate 1.7.0)" || fail "no token must exit 0"
[[ "$out" == *"WINGET_TOKEN is not set"* ]] || fail "no token must say so: $out"
"$SCRIPT" "$TMP" Reothor.Elevate 2>/dev/null && fail "wrong argument count must fail" || true

echo "submit-winget: all tests passed"
