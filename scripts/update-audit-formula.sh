#!/usr/bin/env bash
# Regenerate Formula/elevate-audit.rb for a published release.
#
# Usage: scripts/update-audit-formula.sh <version> <owner/repo> <dist dir>
#   dist dir holds elevate-audit-<version>-<rid>.tar.gz.sha256 for osx-arm64, osx-x64, linux-x64, linux-arm64
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <version> <owner/repo> <dist dir>" >&2
  exit 2
fi

VERSION="$1"
REPO="$2"
DIST="$3"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/Formula/elevate-audit.rb"
mkdir -p "$(dirname "$OUT")"

sha() {
  local file="$DIST/elevate-audit-$VERSION-$1.tar.gz.sha256"
  test -f "$file" || { echo "missing $file" >&2; exit 1; }
  cut -d' ' -f1 "$file"
}

OSX_ARM="$(sha osx-arm64)"
OSX_X64="$(sha osx-x64)"
LINUX_X64="$(sha linux-x64)"
LINUX_ARM="$(sha linux-arm64)"

cat > "$OUT" <<EOF
class ElevateAudit < Formula
  desc "Finds standing privileged access in a Microsoft Entra tenant that belongs in PIM"
  homepage "https://github.com/$REPO"
  version "$VERSION"
  license "MIT"

  on_macos do
    on_arm do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-osx-arm64.tar.gz"
      sha256 "$OSX_ARM"
    end
    on_intel do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-osx-x64.tar.gz"
      sha256 "$OSX_X64"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-linux-arm64.tar.gz"
      sha256 "$LINUX_ARM"
    end
    on_intel do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-audit-#{version}-linux-x64.tar.gz"
      sha256 "$LINUX_X64"
    end
  end

  def install
    bin.install "elevate-audit"
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate-audit version")
  end
end
EOF

echo "Wrote $OUT (version $VERSION)"
