#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage:
  scripts/release.sh patch
  scripts/release.sh minor
  scripts/release.sh major
  scripts/release.sh build
  scripts/release.sh 1.0.1.0

Options:
  --skip-tests  Update, commit, and tag without running dotnet test.
  --dry-run     Print the computed release version without changing files.

The script bumps Directory.Build.props, meta.json, and build.yaml, commits the
version bump, pushes main, creates tag v<version>, and pushes the tag. GitHub
Actions builds the ZIP, publishes the GitHub release, and updates manifest.json.
USAGE
}

mode_or_version=""
skip_tests=false
dry_run=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-tests)
      skip_tests=true
      shift
      ;;
    --dry-run)
      dry_run=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      if [[ -n "$mode_or_version" ]]; then
        echo "Unexpected argument: $1" >&2
        usage >&2
        exit 2
      fi
      mode_or_version="$1"
      shift
      ;;
  esac
done

if [[ -z "$mode_or_version" ]]; then
  usage >&2
  exit 2
fi

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

current_version="$(sed -nE 's/.*<Version>([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)<\/Version>.*/\1/p' Directory.Build.props | head -n1)"
if [[ -z "$current_version" ]]; then
  echo "Could not read current version from Directory.Build.props" >&2
  exit 1
fi

IFS=. read -r major minor patch build <<<"$current_version"
case "$mode_or_version" in
  major)
    next_version="$((major + 1)).0.0.0"
    ;;
  minor)
    next_version="$major.$((minor + 1)).0.0"
    ;;
  patch)
    next_version="$major.$minor.$((patch + 1)).0"
    ;;
  build)
    next_version="$major.$minor.$patch.$((build + 1))"
    ;;
  *)
    next_version="$mode_or_version"
    ;;
esac

if [[ ! "$next_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must be four numeric components, e.g. 1.0.1.0" >&2
  exit 1
fi

if [[ "$next_version" == "$current_version" ]]; then
  echo "Next version equals current version: $next_version" >&2
  exit 1
fi

tag="v$next_version"

if $dry_run; then
  echo "Current version: $current_version"
  echo "Next version:    $next_version"
  echo "Tag:             $tag"
  exit 0
fi

if [[ "$(git branch --show-current)" != "main" ]]; then
  echo "Release must be run from main." >&2
  exit 1
fi

if ! git diff --cached --quiet; then
  echo "Staged changes exist. Commit or unstage them before releasing." >&2
  exit 1
fi

if ! git diff --quiet -- . ':(exclude).gitignore'; then
  echo "Uncommitted changes exist outside .gitignore. Commit or stash them before releasing." >&2
  exit 1
fi

if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
  echo "Local tag already exists: $tag" >&2
  exit 1
fi

git fetch origin main --tags

if [[ "$(git rev-parse HEAD)" != "$(git rev-parse origin/main)" ]]; then
  echo "Local main does not match origin/main. Pull/rebase before releasing." >&2
  exit 1
fi

if git ls-remote --exit-code --tags origin "refs/tags/$tag" >/dev/null 2>&1; then
  echo "Remote tag already exists: $tag" >&2
  exit 1
fi

VERSION="$next_version" perl -0pi -e '
  s|<Version>[^<]+</Version>|<Version>$ENV{VERSION}</Version>|;
  s|<AssemblyVersion>[^<]+</AssemblyVersion>|<AssemblyVersion>$ENV{VERSION}</AssemblyVersion>|;
  s|<FileVersion>[^<]+</FileVersion>|<FileVersion>$ENV{VERSION}</FileVersion>|;
  s|<PackageVersion>[^<]+</PackageVersion>|<PackageVersion>$ENV{VERSION}</PackageVersion>|;
' Directory.Build.props

VERSION="$next_version" perl -0pi -e 's/"version":\s*"[^"]+"/"version": "$ENV{VERSION}"/' meta.json

VERSION="$next_version" perl -0pi -e '
  s/version:\s*"[^"]+"/version: "$ENV{VERSION}"/;
  s|imageUrl:\s*"https://raw\.githubusercontent\.com/watchoffit/watchoffit-jellyfin-plugin/v[^/]+/assets/watchoffit\.png"|imageUrl: "https://raw.githubusercontent.com/watchoffit/watchoffit-jellyfin-plugin/v$ENV{VERSION}/assets/watchoffit.png"|;
' build.yaml

if ! $skip_tests; then
  dotnet test Jellyfin.Plugin.Watchoffit.sln
fi

git add Directory.Build.props meta.json build.yaml
git commit -m "Bump Jellyfin plugin to $next_version"
git push origin main
git tag "$tag"
git push origin "$tag"

echo "Released $tag. Watch the GitHub Actions Release workflow for package and manifest publishing."
