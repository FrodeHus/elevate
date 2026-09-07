#!/usr/bin/env bash
# Move the entries under "## [Unreleased]" in CHANGELOG.md to a new versioned section.
#
# Usage: scripts/cut-changelog.sh <version> [date] [owner/repo]
#   version     the release version without the leading "v", e.g. 1.2.6
#   date        the release date, YYYY-MM-DD; today (UTC) when omitted
#   owner/repo  GitHub repository for the comparison links; FrodeHus/elevate when omitted
#
# Fails when the Unreleased section is empty or the version already has a section, so a
# release cannot be cut with nothing to say or cut twice.
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "usage: $0 <version> [date] [owner/repo]" >&2
  exit 2
fi

VERSION="$1"
DATE="${2:-$(date -u +%Y-%m-%d)}"
REPO="${3:-FrodeHus/elevate}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FILE="$ROOT/CHANGELOG.md"

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "version must be x.y.z, got '$VERSION'" >&2
  exit 2
fi
if grep -q "^## \[$VERSION\]" "$FILE"; then
  echo "CHANGELOG.md already has a section for $VERSION" >&2
  exit 1
fi

# The Unreleased body: everything between its heading and the next "## " heading, ignoring blank lines.
BODY="$(awk '/^## \[Unreleased\]/{f=1; next} /^## /{f=0} f' "$FILE")"
if [ -z "$(printf '%s' "$BODY" | grep -v '^[[:space:]]*$' || true)" ]; then
  echo "CHANGELOG.md has nothing under Unreleased; add the release notes first" >&2
  exit 1
fi

PREVIOUS="$(grep -m1 -oE '^## \[[0-9]+\.[0-9]+\.[0-9]+\]' "$FILE" | tr -d '#[] ' || true)"
if [ -z "$PREVIOUS" ]; then
  echo "could not find the previous version heading in CHANGELOG.md" >&2
  exit 1
fi

TMP="$(mktemp)"
awk -v v="$VERSION" -v d="$DATE" -v prev="$PREVIOUS" -v repo="$REPO" '
  /^## \[Unreleased\]/ { print; print ""; print "## [" v "] - " d; next }
  /^\[Unreleased\]: / {
    print "[Unreleased]: https://github.com/" repo "/compare/v" v "...HEAD"
    print "[" v "]: https://github.com/" repo "/compare/v" prev "...v" v
    next
  }
  { print }
' "$FILE" > "$TMP"
# Collapse the blank lines the move can leave between the Unreleased heading and the new one.
awk 'NR==1 || !(/^$/ && p ~ /^$/) { print } { p=$0 }' "$TMP" > "$FILE"
rm -f "$TMP"
echo "Cut $VERSION ($DATE) from Unreleased; previous version $PREVIOUS."
