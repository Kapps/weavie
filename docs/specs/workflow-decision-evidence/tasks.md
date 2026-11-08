# Workflow decision evidence — tasks

Build in this order against [design.md](design.md). Numbers refer to [requirements.md](requirements.md).
Each group leaves a working, testable increment; update affected documentation with the behavior it ships.

## 1. Unbounded typed inference

**Requirements:** 7. **Dependencies:** none.

- [x] Remove `MaxPromptBytes` from typed-inference options, validation, callers, and test factories; retain
  unrelated correction-corpus limits and existing image/output/time policies.
- [x] Update failure descriptions and affected inference documentation to reflect provider-owned limits.
- [x] Extend isolated Claude/ACP process fakes to verify complete large UTF-8 payloads and simulate rejection.
- [x] Add service and real-process tests for intact multi-megabyte input on both transports and visible
  provider rejection; add the focused real-app journey needed for the demonstration.

**Video:** in the real app, run a workflow decision over a large text output using each inference transport.
Show successful routing, then a simulated provider rejection reaching the user's question. Accompany the
clip with asserted payload receipts from the isolated fakes; the video alone does not prove byte integrity.

## 2. Confidence and described choices

**Requirements:** 1, 2, 3, 8. **Dependencies:** 1.

- [x] Add finite numeric settings support through coercion, persistence, catalog output, and typed access;
  register live `workflow.decisionConfidence` with default `0.8` and range validation.
- [x] Extend workflow schema, reader, and definitions for `confidence:` and both choice forms; validate
  user-only/invalid overrides and descriptions.
- [x] Replace the choice/`unsure` result contract with choice plus confidence or refusal/failure. Add the
  decision plan/prepared request contracts; at this increment file evidence is empty.
- [x] Request numeric utility confidence with strict source-generated JSON, pass choice descriptions,
  translate refusals/errors, and update every caller/test double together.
- [x] Capture each attempt's threshold, enforce engine validation/escalation, and preserve loops, human
  answers, feedback, and stale-result protection. Wire the live setting through shared Hosting.
- [x] Add focused schema/settings/adapter/engine tests and real-app confidence journeys; update the
  confidence/choice portions of the workflow spec and guidance.

**Video:** use controlled answers to show `0.8` advancing and `0.79` asking the user, change the live setting
and show its next-decision effect, then demonstrate an override and described choices. Show refusal/invalid
answer escalation and a human answer continuing the run. Cover `if` and `then` across the scenarios.

## 3. Explicit file evidence

**Requirements:** 4, 5, 6. **Dependencies:** 1, 2.

- [x] Extract shared declared-reference lookup and worktree path resolution; retain initial `files`
  validation and existing missing-value behavior.
- [x] Build plans from explicit `files` references only, including inputs and earlier outputs; normalize
  and deduplicate paths while preserving the question, output values, summary, and feedback.
- [x] Add the engine file-reader contract and Core implementation over `IFileSystem.TryReadAllText`;
  translate binary/missing/access failures into named unreadable results.
- [x] Extend the pending attempt to prepare all evidence before inference, outside the runner lock.
  Extract its file, implement safe cancellation/disposal, and inject the reader through shared Hosting.
- [x] Pass complete prepared evidence to the utility adapter; keep bodies out of persistence and reread
  files/settings on new attempts after activation.
- [x] Add reader/selection/lifecycle tests and deterministic full-stack journeys for current contents,
  mixed unreadable evidence, no automatic reads, cancellation, and zero calls on failed preparation.
  Update file-evidence documentation and guidance.

**Video:** show a document in the editor and a model decision routing from its contents; edit it and rerun
to demonstrate current reads. Exercise a file input and an earlier-stage output. Show a mixed text/video
reference and a deleted file escalating with the offending path, then answer as the user. Use fake-call
assertions to prove no partial-evidence call and no reads from unreferenced paths.

## 4. Advisory authoring

**Requirements:** 9, 10. **Dependencies:** 2, 3.

- [x] Add positioned warnings for unreferenced `files` outputs and recognizable literal/composed paths
  in model questions, reusing parsed templates and declared-reference lookup.
- [x] Test warning-only validity, runnable workflows, user-decision exclusions, path examples/false
  positives, and continued structural-error rejection.
- [x] Finish `author-workflow`, schema guidance, and `docs/specs/workflows.md`: exact evidence visibility,
  observable single-concern questions, judgment-based warnings, confidence, and choice descriptions.
  Remove remaining obsolete `unsure`/no-file-content claims; update settings docs for numeric values.
- [x] Add a real-app authoring journey using schema/validation tools, verify intentional warnings can
  remain unresolved, and check the completed feature against requirements 1–10.

**Video:** in the real app, inspect the authoring guidance and validate a workflow with both evidence
warnings. Show that it still runs, then replace a literal path with a `files` reference and show the
relevant warning disappear. Show a structural error remaining invalid.

For every group: focused tests and applicable formatters must pass, obtain the required fresh-context
review, and make one group commit as directed by the build stage. The prove stage uses the repo's tester
or recording skill, captures the actual app, and transcodes each WebM to H.264 MP4. Keep recording scripts
and clips untracked; report each MP4 by repo-relative path. Full-suite checks run in PR CI; no merge is
authorized by this checklist.
