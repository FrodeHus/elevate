#!/usr/bin/env bash
# Move the entries under "## [Unreleased]" in a changelog to a new versioned section.
#
# Usage: scripts/cut-changelog.sh [--file <path>] [--tag-prefix <prefix>] <version> [date] [owner/repo]
#   --file      the changelog, relative to the repository root; CHANGELOG.md (the app) when omitted.
#               The audit tool's is audit/CHANGELOG.md.
#   --tag-prefix what precedes the version in this changelog's git tags; "v" when omitted.
#               The audit tool's tags are audit-v<x.y.z>.
#   version     the release version without the tag prefix, e.g. 1.2.6
#   date        the release date, YYYY-MM-DD; today (UTC) when omitted
#   owner/repo  GitHub repository for the comparison links; FrodeHus/elevate when omitted
#
# The new version's comparison link starts where the Unreleased link started, so the first cut
# after a changelog split compares against whatever tag that changelog last shipped under.
#
# Fails when the Unreleased section is empty or the version already has a section, so a
# release cannot be cut with nothing to say or cut twice.
set -euo pipefail

usage() { echo "usage: $0 [--file <path>] [--tag-prefix <prefix>] <version> [date] [owner/repo]" >&2; exit 2; }

FILE_REL="CHANGELOG.md"
PREFIX="v"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --file) [ "$#" -ge 2 ] || usage; FILE_REL="$2"; shift 2 ;;
    --tag-prefix) [ "$#" -ge 2 ] || usage; PREFIX="$2"; shift 2 ;;
    --) shift; break ;;
    -*) usage ;;
    *) break ;;
  esac
done
[ "$#" -ge 1 ] || usage

VERSION="$1"
DATE="${2:-$(date -u +%Y-%m-%d)}"
REPO="${3:-FrodeHus/elevate}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FILE="$ROOT/$FILE_REL"

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "version must be x.y.z, got '$VERSION'" >&2
  exit 2
fi
if [ ! -f "$FILE" ]; then
  echo "$FILE_REL not found" >&2
  exit 1
fi
if grep -q "^## \[$VERSION\]" "$FILE"; then
  echo "$FILE_REL already has a section for $VERSION" >&2
  exit 1
fi

# The Unreleased body: everything between its heading and the next "## " heading, ignoring blank lines.
BODY="$(awk '/^## \[Unreleased\]/{f=1; next} /^## /{f=0} f' "$FILE")"
if [ -z "$(printf '%s' "$BODY" | grep -v '^[[:space:]]*$' || true)" ]; then
  echo "$FILE_REL has nothing under Unreleased; add the release notes first" >&2
  exit 1
fi

# The base of the new comparison link is the base of the Unreleased link: the tag this changelog
# last shipped under, whatever prefix it had.
BASE="$(grep -m1 -E '^\[Unreleased\]: https://github\.com/[^/]+/[^/]+/compare/[^[:space:]]+\.\.\.HEAD$' "$FILE" \
  | sed -E 's#^.*/compare/(.+)\.\.\.HEAD$#\1#' || true)"
if [ -z "$BASE" ]; then
  echo "could not find the [Unreleased] comparison link in $FILE_REL" >&2
  exit 1
fi

TMP="$(mktemp)"
awk -v v="$VERSION" -v d="$DATE" -v base="$BASE" -v repo="$REPO" -v tag="$PREFIX$VERSION" '
  /^## \[Unreleased\]/ { print; print ""; print "## [" v "] - " d; next }
  /^\[Unreleased\]: / {
    print "[Unreleased]: https://github.com/" repo "/compare/" tag "...HEAD"
    print "[" v "]: https://github.com/" repo "/compare/" base "..." tag
    next
  }
  { print }
' "$FILE" > "$TMP"
# Collapse the blank lines the move can leave between the Unreleased heading and the new one.
awk 'NR==1 || !(/^$/ && p ~ /^$/) { print } { p=$0 }' "$TMP" > "$FILE"
rm -f "$TMP"
echo "Cut $VERSION ($DATE) in $FILE_REL from Unreleased; compared against $BASE."
