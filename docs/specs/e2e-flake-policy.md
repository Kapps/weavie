# Flaky tests

Status: policy
Last updated: 2026-08-21

**A flaky test is a bug report. Fix the bug.**

Retries are off. A flake is a red run someone has to eat, and every one of them is a defect in the product
or in the harness that a real user or a later change will hit for real. There is no such thing as a test
that fails "for no reason" — only one whose reason nobody has found yet.

## Not allowed

Never make a flake go away by weakening what the test proves:

- a retry, a `test.retry`, or "re-ran it and it passed"
- a `test.skip`, a quarantine, or a conditional that excludes the failing platform
- a widened timeout, a loosened assertion, or a `waitForTimeout` sprinkled until it settles
- logging the occurrence and moving on

All of these bury the defect and cost the next person the same investigation from scratch. A wider timeout
is the worst of them: it converts a hard failure into a slow one that comes back later, on someone else's
change.

Widening a wait is legitimate only when the wait is genuinely too short for work that is *known* to take
that long — and then the justification is the measured duration of that work, never "it flaked".

## Fix it instead

1. **Get the failure datum.** The CI failure artifacts carry it: `viewport-layout.json`, `console-errors.txt`,
   `weavie-host.log`, and the Playwright trace. The trace's `trace.zip` holds a screenshot per action and a
   serialized DOM per action — that pair usually settles the question outright, and it beats any amount of
   reasoning from the assertion message.
2. **Find the mechanism, then say it in one sentence.** "The click target is the full-content-height
   `.view-lines`, so Playwright's pre-click reveal scrolls the editor to its last line" is a root cause.
   "Windows CI is slow" is not.
3. **Remove the cause.** Prefer a change that makes the failure impossible by construction over one that
   waits longer for it to pass.
4. **Verify.** If it reproduces locally, prove the fix against the repro. If it only shows up on a hosted
   runner, land the reasoned fix and watch CI across several runs before claiming it.

## Where the reasoning goes

In the code, next to the thing it explains, and only what a reader needs to not reintroduce the bug — state
what the code does and why, not the history of how it got there. A running catalog of past occurrences is
not a substitute for a fix, and a comment that narrates six attempts teaches the next agent that logging is
an acceptable outcome. It isn't.
