#!/usr/bin/env bash
# Publish the local wiki/ directory to the GitHub wiki repository.
#
# Usage: bash scripts/publish_wiki.sh ["commit message"]
#
# Requires: the wiki has at least one page created via the GitHub web UI
# (Repository -> Wiki -> Create the first page), so that <repo>.wiki.git exists.

set -euo pipefail

REPO_SLUG="${WIKI_REPO:-zian-yan/jellyfin-plugin-faststrm}"
REMOTE="https://github.com/${REPO_SLUG}.wiki.git"
SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../wiki" && pwd)"
WORK_DIR="$(mktemp -d)"
MESSAGE="${1:-Update wiki from repo}"

cleanup() { rm -rf "${WORK_DIR}"; }
trap cleanup EXIT

if [ ! -d "${SOURCE_DIR}" ]; then
  echo "error: wiki source directory not found: ${SOURCE_DIR}" >&2
  exit 1
fi

echo "==> Cloning ${REMOTE}"
if ! git clone --depth 1 "${REMOTE}" "${WORK_DIR}/wiki" 2>/dev/null; then
  cat >&2 <<'EOF'
error: the wiki repository could not be cloned.

The GitHub wiki must be initialised once in the browser before it can be
pushed to:
  1. open https://github.com/zian-yan/jellyfin-plugin-faststrm/wiki
  2. click "Create the first page", save any content
  3. re-run this script
EOF
  exit 1
fi

echo "==> Syncing markdown files"
find "${WORK_DIR}/wiki" -maxdepth 1 -name '*.md' -delete
cp "${SOURCE_DIR}"/*.md "${WORK_DIR}/wiki/"

cd "${WORK_DIR}/wiki"
git add -A
if git diff --cached --quiet; then
  echo "==> No changes to publish"
  exit 0
fi

# Fall back to the local git identity (global/repo config) unless overridden by env.
IDENTITY=()
[ -n "${GIT_AUTHOR_NAME:-}" ] && IDENTITY+=(-c "user.name=${GIT_AUTHOR_NAME}")
[ -n "${GIT_AUTHOR_EMAIL:-}" ] && IDENTITY+=(-c "user.email=${GIT_AUTHOR_EMAIL}")

git ${IDENTITY[@]+"${IDENTITY[@]}"} commit -q -m "${MESSAGE}"
git push -q origin HEAD

echo "==> Published: https://github.com/${REPO_SLUG}/wiki"
