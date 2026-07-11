---
name: finish-session
description: Ship this Weavie session's work — commit, open a PR, watch CI to green (fixing any reds), merge, then delete the session. Use when the session's work is done and should land on main.
disable-model-invocation: true
---

# Finish session

Ship the session branch end to end. Hard rule: **delete the session only after the PR is
confirmed merged** — never before, and never with unshipped work.

## 1. Commit and push

- This skill ships a session branch; if HEAD is `main`, stop and say so.
- Commit any uncommitted work with a message describing the change, then `git push -u origin HEAD`.
- Nothing to ship (no commits ahead of `origin/main` and no open PR)? Report that and stop.

## 2. Open (or reuse) the PR

- `gh pr view --json url,state` — reuse the branch's existing open PR if there is one.
- Otherwise `gh pr create` against `main` with a body summarizing the change set.

## 3. Watch CI until it concludes — check every 2 minutes, per check

- Poll `gh pr checks --json name,bucket` **every 2 minutes** and act on each check the moment its
  bucket leaves `pending` — never wait for the whole matrix before looking. The matrix builds
  Linux/macOS/Windows and runs the web e2e on each, so the slowest job takes 10+ minutes; an
  early red (e.g. a Linux formatting failure) is actionable immediately, and waiting for the rest
  wastes the whole window. A Monitor loop that diffs concluded checks per poll fits this exactly.
- On the same cadence, check mergeability: `gh pr view --json mergeable,mergeStateStatus`. If
  `mergeable` is `CONFLICTING` (or `mergeStateStatus` is `DIRTY`), main has moved under the PR —
  merge `origin/main` into the branch, resolve the conflicts, push, and keep watching. Catching
  this mid-run beats discovering it at merge time.
- If a poll fails (network, rate limit), poll again; never assume a result.

## 4. Red? Fix it — always

- Every failing check on this PR is yours to fix, even if it looks pre-existing (repo rule: a green
  pipeline is the bar for done). Read the failure with `gh run view <run-id> --log-failed`, fix it,
  commit, push (the new push cancels the stale run), and return to step 3.
- A flaky test is never accepted (repo rule: hiding one is a silent fallback). Re-running with
  `gh run rerun <run-id> --failed` only diagnoses — whether or not the retry passes, root-cause
  the flake and fix it before merging.

## 5. Merge

- Only once every check is green: `gh pr merge --merge` (the repo uses merge commits).
- Confirm with `gh pr view --json state` → `MERGED`. If merging is blocked (review required,
  conflicts), resolve it or report back — do not proceed to deletion.

## 6. Delete the session (last)

- Print the wrap-up first — PR link and what landed — because this step tears down the session
  (including this Claude) immediately.
- Get *your own* session id from the `mcp__weavie__currentSession` MCP tool (it returns
  `{"id":"…"}`). Delete targets an explicit id and no longer defaults to the active session — the
  user may have a *different* session focused, so never rely on a no-arg delete.
- Then run the Weavie command `weavie.session.delete` via the `mcp__weavie__runCommand` MCP tool,
  passing that id as `args` (i.e. `{"id":"<your-session-id>"}`). It keeps the branch and refuses on
  a dirty worktree — if it refuses, do NOT pass `force`; something is unshipped, find out what.
- If it reports the primary session can't be deleted, the work still shipped — say so and finish.
