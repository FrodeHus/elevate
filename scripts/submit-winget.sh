#!/usr/bin/env bash
# Open the microsoft/winget-pkgs pull request for one generated manifest set.
#
# Usage: scripts/submit-winget.sh <manifest-dir> <package-id> <version>
#   manifest-dir  the directory holding the three <package-id>.*.yaml files (the release
#                 workflow's winget-manifest, winget-cli-manifest or winget-audit-manifest artifact)
#   package-id    e.g. Reothor.Elevate.CLI
#   version       e.g. 1.7.0
#
# Needs WINGET_TOKEN: a classic personal access token with the public_repo scope, for the
# account whose fork of microsoft/winget-pkgs receives the branch. Without it the script says so
# and exits 0, so a release without the token still publishes everything else.
#
# What it does, with `gh` as that account: forks winget-pkgs if the account has no fork, brings
# the fork's master up to date, creates the branch <package-id>-<version> from it, writes the
# manifests at the path winget-pkgs demands (every dot-separated segment of the identifier is a
# directory level: manifests/r/Reothor/Elevate/CLI/1.7.0) through the contents API, and opens the
# pull request. Idempotent: an existing branch is updated, an open pull request for the same
# head is left alone.
set -euo pipefail

UPSTREAM="microsoft/winget-pkgs"

usage() { echo "usage: $0 <manifest-dir> <package-id> <version>" >&2; exit 2; }

# manifests/<partition>/<segment>/<segment>/.../<version>
manifest_path() {
  local id="$1" version="$2"
  local partition
  partition="$(printf '%s' "${id:0:1}" | tr '[:upper:]' '[:lower:]')"
  printf 'manifests/%s/%s/%s' "$partition" "${id//.//}" "$version"
}

pr_title() {
  local id="$1" version="$2" existing="$3"   # existing: "yes" when the package already has a version upstream
  if [ "$existing" = "yes" ]; then printf 'New version: %s version %s' "$id" "$version"
  else printf 'New package: %s version %s' "$id" "$version"; fi
}

pr_body() {
  local id="$1" version="$2" tag="$3" repo="$4"
  cat <<MD
## 📖 Description

$id version $version, generated and validated (\`winget validate\`) by the release workflow of https://github.com/$repo; installers are the assets of https://github.com/$repo/releases/tag/$tag, Authenticode-signed with a Certum certificate ("Open Source Developer Frode Hus").

## ✅ Checklist

- [x] Signed the [Contributor License Agreement](https://cla.opensource.microsoft.com)
- [ ] Linked to an issue (if applicable)

## 📦 Manifest Checklist

- [x] Checked that there aren't other open [pull requests](https://github.com/microsoft/winget-pkgs/pulls) for the same manifest update/change
- [x] This PR only modifies one (1) manifest
- [x] Validated manifest locally with \`winget validate --manifest <path>\` ([validation guide](https://github.com/microsoft/winget-pkgs/blob/master/doc/ValidationFailureGuide.md)) — in the release workflow, on the runner that built the installers
- [ ] Tested manifest locally with \`winget install --manifest <path>\` — not part of the automated submission; the installer itself is exercised by the release checks
- [x] Manifest conforms to the declared schema version
MD
}

main() {
  [ "$#" -eq 3 ] || usage
  local dir="$1" id="$2" version="$3"
  [ -d "$dir" ] || { echo "::error::$dir is not a directory"; exit 1; }
  if [ -z "${WINGET_TOKEN:-}" ]; then
    echo "WINGET_TOKEN is not set; not submitting $id $version to $UPSTREAM."
    exit 0
  fi
  export GH_TOKEN="$WINGET_TOKEN"
  local files=("$dir/$id.yaml" "$dir/$id.installer.yaml" "$dir/$id.locale.en-US.yaml")
  for f in "${files[@]}"; do [ -f "$f" ] || { echo "::error::$f is missing"; exit 1; }; done

  local owner fork branch path tag repo
  owner="$(gh api user -q .login)"
  fork="$owner/winget-pkgs"
  branch="$id-$version"
  path="$(manifest_path "$id" "$version")"
  tag="${TAG:-v$version}"
  repo="${GITHUB_REPOSITORY:-FrodeHus/elevate}"

  if ! gh api "repos/$fork" >/dev/null 2>&1; then
    echo "Forking $UPSTREAM as $fork"
    gh repo fork "$UPSTREAM" --clone=false >/dev/null
    sleep 10
  fi
  gh api -X POST "repos/$fork/merge-upstream" -f branch=master >/dev/null 2>&1 || true

  if [ -n "$(gh pr list --repo "$UPSTREAM" --head "$owner:$branch" --state open --json number -q '.[0].number')" ]; then
    echo "A pull request from $owner:$branch is already open; nothing to do."
    exit 0
  fi

  if ! gh api "repos/$fork/git/ref/heads/$branch" >/dev/null 2>&1; then
    local base
    base="$(gh api "repos/$fork/git/ref/heads/master" -q .object.sha)"
    gh api -X POST "repos/$fork/git/refs" -f ref="refs/heads/$branch" -f sha="$base" >/dev/null
    echo "Created $fork:$branch from master ($base)"
  fi

  local existing="no"
  gh api "repos/$UPSTREAM/contents/$(dirname "$path")" >/dev/null 2>&1 && existing="yes"
  local title
  title="$(pr_title "$id" "$version" "$existing")"

  local name sha content payload
  for f in "${files[@]}"; do
    name="$(basename "$f")"
    sha="$(gh api "repos/$fork/contents/$path/$name?ref=$branch" -q .sha 2>/dev/null || true)"
    content="$(base64 < "$f" | tr -d '\n')"
    payload="$(jq -n --arg m "$title" --arg b "$branch" --arg c "$content" --arg s "$sha" \
      '{message: $m, branch: $b, content: $c} + (if $s == "" then {} else {sha: $s} end)')"
    printf '%s' "$payload" | gh api -X PUT "repos/$fork/contents/$path/$name" --input - -q '.content.path'
  done

  local url
  url="$(gh pr create --repo "$UPSTREAM" --base master --head "$owner:$branch" --title "$title" --body "$(pr_body "$id" "$version" "$tag" "$repo")")"
  echo "Opened $url"
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "- winget-pkgs: [$title]($url)" >> "$GITHUB_STEP_SUMMARY"
  return 0
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
