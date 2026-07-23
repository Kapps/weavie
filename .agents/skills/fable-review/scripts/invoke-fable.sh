#!/usr/bin/env bash
set -euo pipefail

usage() {
	printf 'Usage: %s plan [review request]\n' "$0" >&2
	printf '       %s change [--base <ref>] [review request]\n' "$0" >&2
	exit 2
}

mode="${1:-}"
[[ "$mode" == "plan" || "$mode" == "change" ]] || usage
shift

base="origin/main"
options_ended=0
if [[ "$mode" == "change" ]]; then
	while (( $# > 0 )); do
		case "$1" in
			--base)
				(( $# >= 2 )) || usage
				base="$2"
				shift 2
				;;
			--)
				options_ended=1
				shift
				break
				;;
			--*)
				printf "Unknown option '%s'.\n" "$1" >&2
				usage
				;;
			*)
				break
				;;
		esac
	done
	if (( options_ended == 0 )); then
		for argument in "$@"; do
			if [[ "$argument" == "--base" || "$argument" == --base=* ]]; then
				printf '%s\n' 'Place --base before the review request.' >&2
				exit 2
			fi
		done
	fi
fi

if (( $# > 0 )); then
	request="$*"
elif [[ ! -t 0 ]]; then
	request="$(cat)"
else
	printf 'A review request is required on stdin or as an argument.\n' >&2
	exit 2
fi

if [[ -z "${request//[[:space:]]/}" ]]; then
	printf 'The review request cannot be empty.\n' >&2
	exit 2
fi

command -v claude >/dev/null 2>&1 || {
	printf 'Claude Code CLI (claude) is not available.\n' >&2
	exit 127
}
command -v node >/dev/null 2>&1 || {
	printf 'Node.js is required to verify Claude Code result metadata.\n' >&2
	exit 127
}

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || {
	printf 'Run this skill from inside a Git repository.\n' >&2
	exit 2
}
cd "$repo_root"

if [[ "$mode" == "plan" ]]; then
	rubric='You are Fable performing an independent, read-only architecture and implementation-plan review. Do not edit files or implement the plan. Read AGENTS.md completely, then inspect the relevant repository source and documentation so the review is grounded in the actual system. Challenge ownership, lifecycle, concurrency, persistence, failure handling, security, migration, and testing assumptions where applicable. Lead with a verdict: approve, revise, or reject. List blocking findings before non-blocking suggestions, cite repo-relative path:line evidence, and finish with a concrete recommended revision. Say explicitly when no blocking findings exist.'
else
	rubric='You are Fable performing an independent, read-only code review. Do not edit files or implement fixes. Read AGENTS.md completely. The caller supplies authoritative git status plus tracked and untracked patch content below; inspect relevant surrounding source and tests with the read-only tools. Review only the supplied change set, including every untracked file. Prioritize correctness, data loss, security, lifecycle and cross-session routing, concurrency, failure handling, and repository standards; ignore unrelated pre-existing issues. Report findings in severity order with repo-relative path:line evidence and explain the concrete failure mode. Say explicitly when no findings exist.'
	base_commit="$(git rev-parse --verify --end-of-options "${base}^{commit}" 2>/dev/null)" || {
		printf "Cannot resolve review base '%s' to a commit.\n" "$base" >&2
		exit 2
	}
fi

review_temp="$(mktemp -d)" || {
	printf 'Cannot create a temporary review directory.\n' >&2
	exit 1
}
context_file="$review_temp/context"
untracked_file="$review_temp/untracked"
cleanup() {
	rm -f -- "$context_file" "$untracked_file"
	rmdir -- "$review_temp"
}
trap cleanup EXIT

if [[ "$mode" == "change" ]]; then
	git ls-files --others --exclude-standard -z > "$untracked_file" || {
		printf 'Cannot enumerate untracked files for review.\n' >&2
		exit 1
	}
	if git diff --quiet --no-ext-diff "$base_commit" --; then
		if [[ ! -s "$untracked_file" ]]; then
			printf "Nothing to review against '%s'.\n" "$base" >&2
			exit 2
		fi
	else
		status=$?
		if (( status != 1 )); then
			printf "Cannot inspect changes against '%s' (git diff exited %d).\n" "$base" "$status" >&2
			exit "$status"
		fi
	fi
fi

emit_context() {
	printf '%s\n\nReview request and context:\n%s\n' "$rubric" "$request"
	if [[ "$mode" == "change" ]]; then
		printf '\nReview base: %s (%s)\n\nGit status:\n' "$base" "$base_commit"
		git status --short --untracked-files=all || {
			printf 'Cannot read Git status for review.\n' >&2
			return 1
		}
		printf '\nDiff against review base:\n'
		git diff --no-ext-diff --find-renames "$base_commit" -- || {
			printf "Cannot build the tracked diff against '%s'.\n" "$base" >&2
			return 1
		}
		while IFS= read -r -d '' path; do
			if [[ -L "$path" ]]; then
				printf '\nUntracked symlink: %s -> %s\n' "$path" "$(readlink -- "$path")"
			elif [[ -f "$path" ]]; then
				if git diff --no-ext-diff --no-index -- /dev/null "$path"; then
					printf '\nUntracked empty file: %s\n' "$path"
				else
					status=$?
					if (( status != 1 )); then
						printf "Cannot build the untracked diff for '%s' (git diff exited %d).\n" "$path" "$status" >&2
						return "$status"
					fi
				fi
			else
				printf "Cannot review non-regular untracked path '%s'.\n" "$path" >&2
				return 1
			fi
		done < "$untracked_file"
	fi
}

emit_context > "$context_file" || {
	printf 'Review context assembly failed; Fable was not invoked.\n' >&2
	exit 1
}

claude \
	-p \
	--model fable \
	--effort xhigh \
	--tools 'Read,Grep,Glob' \
	--allowedTools 'Read,Grep,Glob' \
	--no-session-persistence \
	--output-format json \
	< "$context_file" \
	| node "$script_dir/verify-result.mjs"
