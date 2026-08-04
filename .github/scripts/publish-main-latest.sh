#!/usr/bin/env bash
set -euo pipefail

publish_main_latest() {
  if [ "$#" -ne 6 ]; then
    echo "usage: publish-main-latest.sh BUILT STAGING API_URL REPOSITORY RUN_NUMBER ASSET_DIR" >&2
    return 2
  fi

  local built=$1
  local staging=$2
  local api_url=$3
  local repository=$4
  local run_number=$5
  local asset_dir=$6
  local response
  local api="$api_url/repos/$repository"
  response=$(mktemp)

  api_status() {
    curl --silent --show-error --output "$response" --write-out '%{http_code}' \
      --header "Authorization: Bearer $GH_TOKEN" \
      --header "X-GitHub-Api-Version: 2022-11-28" "$1"
  }

  cleanup_staging() {
    local cleanup_status=0
    local release_id
    local release_ids
    local status

    if ! release_ids=$(gh api --paginate "repos/$repository/releases?per_page=100" \
      --jq ".[] | select(.draft and .tag_name == \"$staging\") | .id"); then
      echo "::error::Staging draft lookup failed"
      return 1
    fi
    while IFS= read -r release_id; do
      if [ -n "$release_id" ] && ! gh api --method DELETE "repos/$repository/releases/$release_id"; then
        cleanup_status=1
      fi
    done <<<"$release_ids"

    if ! status=$(api_status "$api/git/ref/tags/$staging"); then
      echo "::error::Staging tag lookup failed"
      return 1
    fi
    case "$status" in
      200) gh api --method DELETE "repos/$repository/git/refs/tags/$staging" || cleanup_status=1 ;;
      404) ;;
      *) cat "$response"; echo "::error::Staging tag lookup returned HTTP $status"; cleanup_status=1 ;;
    esac
    return "$cleanup_status"
  }

  cleanup_owned_orphan() {
    local status
    local target

    if ! status=$(api_status "$api/releases/tags/main-latest"); then
      echo "::error::main-latest release lookup failed during cleanup"
      return 1
    fi
    case "$status" in
      200) return 0 ;;
      404) ;;
      *) cat "$response"; echo "::error::main-latest release cleanup lookup returned HTTP $status"; return 1 ;;
    esac

    if ! status=$(api_status "$api/git/ref/tags/main-latest"); then
      echo "::error::main-latest tag lookup failed during cleanup"
      return 1
    fi
    case "$status" in
      200)
        if ! target=$(jq -er .object.sha "$response"); then
          echo "::error::main-latest tag response has no target"
          return 1
        fi
        if [ "$target" = "$built" ]; then
          gh api --method DELETE "repos/$repository/git/refs/tags/main-latest"
        fi
        ;;
      404) ;;
      *) cat "$response"; echo "::error::main-latest tag cleanup lookup returned HTTP $status"; return 1 ;;
    esac
  }

  finish() {
    local original_status=$1
    local cleanup_status=0
    trap - EXIT
    if [ "$original_status" -ne 0 ] && ! cleanup_owned_orphan; then
      echo "::error::Failed to clean orphaned main-latest tag"
      cleanup_status=1
    fi
    if ! cleanup_staging; then
      echo "::error::Failed to clean staging release $staging"
      cleanup_status=1
    fi
    rm -f "$response"
    if [ "$original_status" -eq 0 ] && [ "$cleanup_status" -ne 0 ]; then
      original_status=1
    fi
    exit "$original_status"
  }

  if ! cleanup_staging; then
    rm -f "$response"
    return 1
  fi
  trap 'finish $?' EXIT

  gh release create "$staging" --draft --prerelease \
    --target "$built" \
    --title "main latest (build $run_number)" \
    --notes "Rolling prerelease of green main ($built), build $run_number. weavie-runner-linux-x64.tar.gz is the runner+worker bundle consumed by weavie-runner --auto-update (extract into ~/.weavie/runner and launch current/Weavie.Runner); the weavie-{linux,win,osx}-* archives are downloadable app binaries. See docs/specs/runner-auto-update.md." \
    "$asset_dir/weavie-runner-linux-x64.tar.gz" \
    "$asset_dir/weavie-linux-x64.tar.gz" \
    "$asset_dir/weavie-win-x64.zip" \
    "$asset_dir/weavie-osx-arm64.zip"

  local main
  local main_relation
  main=$(gh api "repos/$repository/git/ref/heads/main" --jq .object.sha)
  main_relation=$(gh api "repos/$repository/compare/$built...$main" --jq .status)
  case "$main_relation" in
    ahead|identical) ;;
    behind|diverged)
      echo "::notice::Skipping $built because it is no longer on main"
      exit 0
      ;;
    *)
      echo "::error::Unexpected relation to main: $main_relation"
      exit 1
      ;;
  esac

  local current
  local published_relation
  local relation
  local status
  status=$(api_status "$api/git/ref/tags/main-latest")
  case "$status" in
    200)
      current=$(jq -er .object.sha "$response")
      published_relation=$(gh api "repos/$repository/compare/$current...$main" --jq .status)
      case "$published_relation" in
        behind|diverged)
          echo "::notice::Replacing $current because it is no longer on main"
          ;;
        ahead|identical)
          relation=$(gh api "repos/$repository/compare/$current...$built" --jq .status)
          case "$relation" in
            ahead) ;;
            identical)
              status=$(api_status "$api/releases/tags/main-latest")
              case "$status" in
                200)
                  echo "::notice::main-latest already points to $built"
                  exit 0
                  ;;
                404) echo "::notice::Repairing orphaned main-latest tag at $built" ;;
                *) cat "$response"; echo "::error::Release lookup failed with HTTP $status"; exit 1 ;;
              esac
              ;;
            behind)
              status=$(api_status "$api/releases/tags/main-latest")
              case "$status" in
                200)
                  echo "::notice::Skipping stale $built; main-latest is already $current"
                  exit 0
                  ;;
                404)
                  echo "::error::main-latest points to newer $current but has no published release"
                  exit 1
                  ;;
                *) cat "$response"; echo "::error::Release lookup failed with HTTP $status"; exit 1 ;;
              esac
              ;;
            *)
              echo "::error::Unexpected commit relation: $relation"
              exit 1
              ;;
          esac
          ;;
        *)
          echo "::error::Unexpected published commit relation: $published_relation"
          exit 1
          ;;
      esac
      ;;
    404) ;;
    *) cat "$response"; echo "::error::Tag lookup failed with HTTP $status"; exit 1 ;;
  esac

  status=$(api_status "$api/releases/tags/main-latest")
  case "$status" in
    200)
      local release_id
      release_id=$(jq -er .id "$response")
      gh api --method DELETE "repos/$repository/releases/$release_id"
      ;;
    404) ;;
    *) cat "$response"; echo "::error::Release lookup failed with HTTP $status"; exit 1 ;;
  esac

  status=$(api_status "$api/git/ref/tags/main-latest")
  case "$status" in
    200)
      gh api --method PATCH "repos/$repository/git/refs/tags/main-latest" \
        --raw-field sha="$built" \
        --field force=true >/dev/null
      ;;
    404)
      gh api --method POST "repos/$repository/git/refs" \
        --raw-field ref=refs/tags/main-latest \
        --raw-field sha="$built" >/dev/null
      ;;
    *) cat "$response"; echo "::error::Tag lookup failed with HTTP $status"; exit 1 ;;
  esac
  gh release edit "$staging" --tag main-latest --draft=false
  finish 0
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  publish_main_latest "$@"
fi
