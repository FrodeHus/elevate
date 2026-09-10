#!/usr/bin/env bash
# Regenerate Casks/elevate.rb for a published release.
#
# Usage: scripts/update-cask.sh <version> <sha256> <owner/repo> [signed]
#   version   release version without the leading "v", e.g. 1.0.0
#   sha256    SHA-256 of Elevate-<version>.pkg
#   owner/repo  GitHub repository the release lives in, e.g. FrodeHus/elevate
#   signed    "1" when the pkg is Developer ID signed and notarized; anything
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
  caveats "This build is not signed with a Developer ID. If macOS blocks the app on first launch, open System Settings > Privacy & Security, scroll to the message about Elevate and click Open Anyway."'
fi

cat > "$OUT" <<EOF
cask "elevate" do
  version "$VERSION"
  sha256 "$SHA256"

  url "https://github.com/$REPO/releases/download/v#{version}/Elevate-#{version}.pkg"
  name "Elevate"
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the menu bar and the terminal"
  homepage "https://github.com/$REPO"

  depends_on macos: :tahoe

  # The installer package puts Elevate.app in /Applications and links /usr/local/bin/elevate to
  # the CLI bundled in Elevate.app/Contents/Helpers (Apple Silicon; Intel Macs use the
  # elevate-cli archive). A pkg needs sudo, which is why Homebrew asks for a password.
  pkg "Elevate-#{version}.pkg"

  uninstall quit:    "no.reothor.elevate",
            pkgutil: "no.reothor.elevate",
            delete:  "/usr/local/bin/elevate"

  zap trash: [
    "~/Library/Application Support/Elevate",
    "~/Library/Preferences/no.reothor.elevate.plist",
  ]$CAVEATS
end
EOF

echo "Wrote $OUT (version $VERSION)"
