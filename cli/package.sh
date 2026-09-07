#!/usr/bin/env bash
# Build and package the CLI for one platform. Two steps, so a code-signing step can run between
# them in the release workflow:
#
#   cli/package.sh publish <version> <rid>   publishes one self-contained file to cli/dist/<rid>/
#   cli/package.sh archive <version> <rid>   wraps it as cli/dist/elevate-cli-<version>-<rid>.tar.gz
#                                            (.zip on Windows) and writes the .sha256 next to it
#
# Runs under bash on Linux, macOS and Windows (Git Bash). RIDs: linux-x64, linux-arm64, osx-arm64,
# osx-x64, win-x64, win-arm64. Self-contained single-file publishing cross-compiles across
# operating systems, so all six can be produced on one machine; the workflow still builds each
# platform on its own runner so the macOS and Windows binaries can be signed there.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 publish|archive <version> <rid>" >&2
  exit 2
fi

STEP="$1"
VERSION="$2"
RID="$3"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STAGE="$ROOT/dist/$RID"
NAME="elevate-cli-$VERSION-$RID"

case "$RID" in
  win-*) EXE="elevate.exe" ;;
  linux-*|osx-*) EXE="elevate" ;;
  *) echo "unknown rid '$RID'" >&2; exit 2 ;;
esac

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1"
  else
    shasum -a 256 "$1"
  fi
}

case "$STEP" in
  publish)
    rm -rf "$STAGE"
    dotnet publish "$ROOT/src/Elevate.Cli/Elevate.Cli.csproj" -c Release -r "$RID" -o "$STAGE" -p:Version="$VERSION" --nologo
    # Only the executable ships; the Core symbols next to it are not part of the archive.
    rm -f "$STAGE"/*.pdb
    test -f "$STAGE/$EXE"
    echo "Published $STAGE/$EXE"
    ;;
  archive)
    test -f "$STAGE/$EXE" || { echo "$STAGE/$EXE missing; run 'publish' first" >&2; exit 1; }
    cd "$ROOT/dist"
    case "$RID" in
      win-*)
        rm -f "$NAME.zip"
        # Relative paths: this runs under Git Bash on Windows, whose /c/... paths PowerShell cannot resolve.
        pwsh -NoProfile -Command "Compress-Archive -Path '$RID/$EXE' -DestinationPath '$NAME.zip' -CompressionLevel Optimal"
        ARCHIVE="$NAME.zip"
        ;;
      *)
        chmod +x "$STAGE/$EXE"
        rm -f "$NAME.tar.gz"
        tar -C "$STAGE" -czf "$NAME.tar.gz" "$EXE"
        ARCHIVE="$NAME.tar.gz"
        ;;
    esac
    (cd "$ROOT/dist" && sha256 "$ARCHIVE" | tee "$ARCHIVE.sha256")
    ;;
  *)
    echo "unknown step '$STEP'; use publish or archive" >&2
    exit 2
    ;;
esac
