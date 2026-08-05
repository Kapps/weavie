#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$script_dir/publish-main-latest.sh"

assert_operation() {
  local operations=$1
  local expected=$2
  if ! grep -Fqx "$expected" "$operations"; then
    echo "missing operation: $expected" >&2
    cat "$operations" >&2
    return 1
  fi
}

assert_no_operation() {
  local operations=$1
  local unexpected=$2
  if grep -Fqx "$unexpected" "$operations"; then
    echo "unexpected operation: $unexpected" >&2
    cat "$operations" >&2
    return 1
  fi
}

assert_order() {
  local operations=$1
  shift
  local previous=0
  local expected
  local line
  for expected in "$@"; do
    line=$(grep -nFx "$expected" "$operations" | head -1 | cut -d: -f1)
    if [ -z "$line" ] || [ "$line" -le "$previous" ]; then
      echo "operation out of order: $expected" >&2
      cat "$operations" >&2
      return 1
    fi
    previous=$line
  done
}

run_case() {
  local scenario=$1
  local initial_tag=$2
  local initial_sha=$3
  local initial_release=$4
  local initial_staging=$5
  local main_sha=$6
  local fail_edit=$7
  local expected_status=$8
  local case_dir
  case_dir=$(mktemp -d)
  local operations="$case_dir/operations"
  local output="$case_dir/output"
  : >"$operations"

  set +e
  (
    set -euo pipefail
    tag_exists=$initial_tag
    tag_sha=$initial_sha
    main_release_exists=$initial_release
    staging_release_exists=$initial_staging

    curl() {
      local output_file=
      local url=
      while [ "$#" -gt 0 ]; do
        case "$1" in
          --output)
            output_file=$2
            shift 2
            ;;
          http*)
            url=$1
            shift
            ;;
          *) shift ;;
        esac
      done

      local status
      local body
      case "$url" in
        */git/ref/tags/main-latest-123)
          status=404
          body='{"message":"Not Found"}'
          ;;
        */releases/tags/main-latest)
          if [ "$main_release_exists" -eq 1 ]; then
            status=200
            body='{"id":42}'
          else
            status=404
            body='{"message":"Not Found"}'
          fi
          ;;
        */git/ref/tags/main-latest)
          if [ "$tag_exists" -eq 1 ]; then
            status=200
            body="{\"object\":{\"sha\":\"$tag_sha\"}}"
          else
            status=404
            body='{"message":"Not Found"}'
          fi
          ;;
        *)
          echo "unexpected API lookup: $url" >&2
          return 2
          ;;
      esac
      printf '%s' "$body" >"$output_file"
      printf '%s' "$status"
    }

    jq() {
      local query=$2
      local file=$3
      case "$query" in
        .id) sed -n 's/.*"id":\([0-9]*\).*/\1/p' "$file" ;;
        .object.sha) sed -n 's/.*"sha":"\([^"]*\)".*/\1/p' "$file" ;;
        *) echo "unexpected jq query: $query" >&2; return 2 ;;
      esac
    }

    gh() {
      if [ "$1 $2" = "release create" ]; then
        staging_release_exists=1
        printf '%s\n' CREATE_STAGING >>"$operations"
        return 0
      fi
      if [ "$1 $2" = "release edit" ]; then
        if [ "$fail_edit" -eq 1 ]; then
          printf '%s\n' FAIL_PUBLISH >>"$operations"
          return 1
        fi
        if [ "$tag_exists" -ne 1 ] || [ "$tag_sha" != builtsha ]; then
          echo "main-latest tag is not ready" >&2
          return 2
        fi
        staging_release_exists=0
        main_release_exists=1
        printf '%s\n' PUBLISH_RELEASE >>"$operations"
        return 0
      fi
      if [ "$1" != api ]; then
        echo "unexpected gh command: $*" >&2
        return 2
      fi
      shift

      if [ "$1" = --paginate ]; then
        if [ "$2" != "repos/acme/weavie/releases?per_page=100" ]; then
          echo "unexpected paginated query: $*" >&2
          return 2
        fi
        if [ "$staging_release_exists" -eq 1 ]; then
          printf '%s\n' 99
        fi
        return 0
      fi

      if [ "$1" = --method ]; then
        local method=$2
        local endpoint=$3
        case "$method $endpoint" in
          "DELETE repos/acme/weavie/releases/42")
            main_release_exists=0
            printf '%s\n' DELETE_MAIN_RELEASE >>"$operations"
            ;;
          "DELETE repos/acme/weavie/releases/99")
            staging_release_exists=0
            printf '%s\n' DELETE_STAGING >>"$operations"
            ;;
          "DELETE repos/acme/weavie/git/refs/tags/main-latest")
            tag_exists=0
            printf '%s\n' DELETE_MAIN_TAG >>"$operations"
            ;;
          "PATCH repos/acme/weavie/git/refs/tags/main-latest")
            if [ "$tag_exists" -ne 1 ]; then
              echo "tag does not exist" >&2
              return 1
            fi
            tag_sha=builtsha
            printf '%s\n' UPDATE_MAIN_TAG >>"$operations"
            ;;
          "POST repos/acme/weavie/git/refs")
            if [ "$tag_exists" -eq 1 ]; then
              echo "tag already exists" >&2
              return 1
            fi
            tag_exists=1
            tag_sha=builtsha
            printf '%s\n' CREATE_MAIN_TAG >>"$operations"
            ;;
          *)
            echo "unexpected gh mutation: $method $endpoint" >&2
            return 2
            ;;
        esac
        return 0
      fi

      case "$1" in
        repos/acme/weavie/git/ref/heads/main) printf '%s\n' "$main_sha" ;;
        repos/acme/weavie/compare/builtsha...builtsha) printf '%s\n' identical ;;
        repos/acme/weavie/compare/builtsha...newsha) printf '%s\n' ahead ;;
        repos/acme/weavie/compare/newsha...newsha) printf '%s\n' identical ;;
        repos/acme/weavie/compare/newsha...builtsha) printf '%s\n' behind ;;
        repos/acme/weavie/compare/oldsha...builtsha) printf '%s\n' ahead ;;
        *) echo "unexpected gh query: $*" >&2; return 2 ;;
      esac
    }

    export GH_TOKEN=test-token GH_REPO=acme/weavie
    publish_main_latest \
      builtsha \
      main-latest-123 \
      https://api.github.test \
      acme/weavie \
      77 \
      release-assets
  ) >"$output" 2>&1
  local actual_status=$?
  set -e

  if [ "$actual_status" -ne "$expected_status" ]; then
    echo "$scenario returned $actual_status, expected $expected_status" >&2
    cat "$output" >&2
    cat "$operations" >&2
    return 1
  fi

  case "$scenario" in
    fresh)
      assert_order "$operations" CREATE_STAGING CREATE_MAIN_TAG PUBLISH_RELEASE
      ;;
    replace)
      assert_order "$operations" \
        CREATE_STAGING DELETE_MAIN_RELEASE UPDATE_MAIN_TAG PUBLISH_RELEASE
      ;;
    publish-failure)
      assert_order "$operations" \
        CREATE_STAGING CREATE_MAIN_TAG FAIL_PUBLISH DELETE_MAIN_TAG DELETE_STAGING
      ;;
    repair-orphan)
      assert_order "$operations" \
        CREATE_STAGING UPDATE_MAIN_TAG PUBLISH_RELEASE
      ;;
    already-published)
      assert_operation "$operations" CREATE_STAGING
      assert_operation "$operations" DELETE_STAGING
      assert_no_operation "$operations" DELETE_MAIN_TAG
      assert_no_operation "$operations" PUBLISH_RELEASE
      ;;
    retry-abandoned-draft)
      assert_order "$operations" DELETE_STAGING CREATE_STAGING CREATE_MAIN_TAG PUBLISH_RELEASE
      ;;
    stale-orphan)
      assert_operation "$operations" CREATE_STAGING
      assert_operation "$operations" DELETE_STAGING
      assert_no_operation "$operations" DELETE_MAIN_TAG
      assert_no_operation "$operations" UPDATE_MAIN_TAG
      assert_no_operation "$operations" PUBLISH_RELEASE
      grep -Fq 'points to newer newsha but has no published release' "$output"
      ;;
  esac

  echo "PASS $scenario"
  rm -rf "$case_dir"
}

run_case fresh 0 none 0 0 builtsha 0 0
run_case replace 1 oldsha 1 0 builtsha 0 0
run_case publish-failure 0 none 0 0 builtsha 1 1
run_case repair-orphan 1 builtsha 0 0 builtsha 0 0
run_case already-published 1 builtsha 1 0 builtsha 0 0
run_case retry-abandoned-draft 0 none 0 1 builtsha 0 0
run_case stale-orphan 1 newsha 0 0 newsha 0 1
