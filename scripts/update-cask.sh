#!/usr/bin/env bash
# Regenerate Casks/elevate.rb for a published release.
#
# Usage: scripts/update-cask.sh <version> <sha256> <owner/repo> [signed]
#   version   release version without the leading "v", e.g. 1.0.0
#   sha256    SHA-256 of Elevate-<version>.dmg
#   owner/repo  GitHub repository the release lives in, e.g. FrodeHus/elevate
#   signed    "1" when the DMG is Developer ID signed and notarized; anything
#             else (or omitted) adds the unsigned-build caveats.
set -euo pipefail

if [ "$#" -lt 3 ]; then
  echo "usage: $0 <version> <sha256> <owner/repo> [signed]" >&2
  exit 2
fi

VERSION="$1"
SHA256="$2"
REPO="$3"
SIGNED="${4:-}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/Casks/elevate.rb"
mkdir -p "$(dirname "$OUT")"

CAVEATS=""
if [ "$SIGNED" != "1" ]; then
  CAVEATS='
  caveats "This build is not signed with a Developer ID. After installing, run: xattr -d com.apple.quarantine /Applications/Elevate.app — or open the app once and approve it under System Settings > Privacy & Security > Open Anyway."'
fi

cat > "$OUT" <<EOF
cask "elevate" do
  version "$VERSION"
  sha256 "$SHA256"

  url "https://github.com/$REPO/releases/download/v#{version}/Elevate-#{version}.dmg"
  name "Elevate"
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the menu bar"
  homepage "https://github.com/$REPO"

  depends_on macos: :tahoe

  app "Elevate.app"

  zap trash: [
    "~/Library/Application Support/Elevate",
    "~/Library/Preferences/no.reothor.elevate.plist",
  ]$CAVEATS
end
EOF

echo "Wrote $OUT (version $VERSION)"
