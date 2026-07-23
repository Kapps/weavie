---
name: fable-review
description: Invoke the actual Fable model through the Claude Code CLI (`claude -p --model fable --effort xhigh`) for an independent, read-only review of an implementation plan, architecture/specification, or current code change. Use when the user asks to send something to Fable, wants Fable's opinion or a second opinion, asks Fable to challenge a plan before implementation, or requests a Fable code review of a diff, branch, or working tree. Never substitute a generic subagent or an impersonated Fable response.
---

# Fable Review

Invoke Fable through the bundled wrapper. Fable reviews the real repository with only read-only tools;
Codex remains responsible for evaluating and acting on its findings.

## Run a review

1. Choose `plan` for a proposed plan, design, or specification. Choose `change` for implemented code.
2. Prepare a self-contained request:
   - For a plan, include the plan itself, its intended outcome, and unresolved decisions. Preserve the
     user's proposal rather than priming Fable with an expected verdict.
   - For a change, include the intent and base ref. Use `origin/main` when no other base is specified.
     Fable inspects the working tree and repository itself; do not paste a lossy summary in place of the diff.
3. From the repository root, send the request on stdin or as the remaining arguments. Change reviews
   default to `origin/main`; pass `--base <ref>` to choose another base:

   ```bash
   printf '%s\n' 'Review this plan: ...' | .agents/skills/fable-review/scripts/invoke-fable.sh plan
   .agents/skills/fable-review/scripts/invoke-fable.sh change --base origin/main \
     'Review the current change against origin/main. Intent: ...'
   ```

4. Report that the wrapper invoked `xhigh` effort and verified Fable in Claude Code's result metadata.
   Relay its verdict and findings faithfully, then distinguish any additional Codex assessment from
   Fable's output.

If the wrapper fails, report the command failure and its diagnostics. Never manufacture a Fable review,
rename a generic subagent "Fable," or silently fall back to another model.

## Review contract

The wrapper:

- runs `claude -p --model fable --effort xhigh` with session persistence disabled;
- parses the JSON result, verifies that a Fable model served it, and rejects error results;
- computes change-review status and tracked/untracked diffs before invocation, then exposes only
  Read/Grep/Glob to Fable, making the review structurally read-only without plan-mode scaffolding;
- tells Fable to read `AGENTS.md` and inspect relevant source rather than reviewing prose in isolation;
- separates plan-review and change-review rubrics; and
- exits nonzero when Claude Code or Fable fails, preserving the failure for the caller.
