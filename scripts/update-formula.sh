#!/usr/bin/env bash
# Regenerate Formula/elevate-cli.rb for a published release.
#
# Usage: scripts/update-formula.sh <version> <owner/repo> <dist dir>
#   version   release version without the leading "v", e.g. 1.3.0
#   owner/repo  GitHub repository the release lives in, e.g. FrodeHus/elevate
#   dist dir  directory holding elevate-cli-<version>-<rid>.tar.gz.sha256 for the four
#             Homebrew platforms: osx-arm64, osx-x64, linux-x64, linux-arm64
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <version> <owner/repo> <dist dir>" >&2
  exit 2
fi

VERSION="$1"
REPO="$2"
DIST="$3"

# Fixed date: the first release carrying the deprecation notice, not the run date, so
# re-runs of this script do not move it.
DEPRECATED_ON="2026-09-10"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/Formula/elevate-cli.rb"
mkdir -p "$(dirname "$OUT")"

sha() {
  local file="$DIST/elevate-cli-$VERSION-$1.tar.gz.sha256"
  test -f "$file" || { echo "missing $file" >&2; exit 1; }
  cut -d' ' -f1 "$file"
}

OSX_ARM="$(sha osx-arm64)"
OSX_X64="$(sha osx-x64)"
LINUX_X64="$(sha linux-x64)"
LINUX_ARM="$(sha linux-arm64)"

cat > "$OUT" <<EOF
class ElevateCli < Formula
  desc "Just-in-time Entra, Azure and PIM for Groups activation from the terminal"
  homepage "https://github.com/$REPO"
  version "$VERSION"
  license "MIT"

  # The CLI now ships inside the Elevate cask (via the pkg) and the MSI. This formula is kept for
  # one more release for Linux and Intel Macs; the release archives stay available afterwards.
  deprecate! date: "$DEPRECATED_ON", because: "the elevate CLI is installed by the elevate cask and the macOS pkg; Linux and Intel Macs use the elevate-cli archives from the GitHub release"

  on_macos do
    on_arm do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-cli-#{version}-osx-arm64.tar.gz"
      sha256 "$OSX_ARM"
    end
    on_intel do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-cli-#{version}-osx-x64.tar.gz"
      sha256 "$OSX_X64"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-cli-#{version}-linux-arm64.tar.gz"
      sha256 "$LINUX_ARM"
    end
    on_intel do
      url "https://github.com/$REPO/releases/download/v#{version}/elevate-cli-#{version}-linux-x64.tar.gz"
      sha256 "$LINUX_X64"
    end
  end

  def install
    bin.install "elevate"
    generate_completions_from_executable(bin/"elevate", "completion")
  end

  test do
    assert_match version.to_s, shell_output("#{bin}/elevate --version")
  end
end
EOF

echo "Wrote $OUT (version $VERSION)"
