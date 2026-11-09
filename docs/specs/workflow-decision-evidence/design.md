# Workflow decision evidence — detailed design

Implements [requirements 1–10](requirements.md) using the approved [overview](overview.md).
The engine owns preparation and policy; Core supplies file access and inference. No provider implements
workflow routing, evidence discovery, or confidence thresholds.

## Contracts and ownership

Use the overview's `WorkflowDecisionPlan`, `WorkflowDecisionRequest`, `WorkflowDecisionEvidence`,
`WorkflowDecisionOption`, `WorkflowFileEvidence`, `WorkflowFileRead`, and result variants. All are owned
by `Weavie.Workflows`; the plan is internal. Collections are snapshots for one attempt, not references to
mutable run dictionaries. Public contracts have XML summaries and no optional constructor parameters.

| Boundary | Contract and responsibility |
| --- | --- |
| Definition | `WorkflowQuestion` adds required nullable `double? Confidence`; `WorkflowChoice` adds nullable `Description`. Absence is explicit, not a deserialization default. |
| Transition → runner | `WorkflowEffect.Decide(WorkflowDecisionPlan Plan)` carries rendered question, described options, outputs, summary, explicit file paths, and captured minimum confidence. |
| Engine → filesystem | `IWorkflowFileReader.Read(string relativePath)` returns `WorkflowFileRead.Text` or `Unreadable`. The Core instance is bound to the session worktree. |
| Engine → model | Existing `IWorkflowDecider.DecideAsync(WorkflowDecisionRequest, CancellationToken)` accepts only prepared evidence. No unresolved-file field, threshold, or routing target. |
| Model → engine | `Answered(string Choice, double Confidence)`, `Refused(string Reason)`, or `Failed(string Reason)`. These are in-memory variants, not a new wire/persistence format. |
| Settings → runner | Required `Func<double>` accessor, alongside existing attempt/open-review accessors; sampled when a model decision plan is created. |

The utility model's strict response is `{"choice":"approve","confidence":0.91}`. Use required JSON
members or a required positional constructor so missing confidence cannot deserialize as zero. Retain
source-generated metadata, unknown-member rejection, and required-constructor enforcement. `unsure` is
not requested or reserved: an undeclared label is invalid; any explicitly declared label is ordinary routing.

## Definition parsing and warnings

`WorkflowSchema` remains the authoritative field table. Add a numeric shape with JSON Schema
`type: number`, `minimum: 0`, `maximum: 1`; parse the YAML reader's scalar strings with invariant culture,
rejecting nonfinite values. `confidence` is optional on object-form `if` and decision-form `then`; shorthand
`if` uses the global setting. Semantic validation rejects confidence on `by: user`.

Choice values use the existing `TextOrObjectShape` pattern:

```yaml
choices:
  approve: { then: build, description: Criteria are observable. }
  revise: draft
```

Require `then` in the object form, allow an omitted plain-text `description`, and reject unknown fields.
Normalize both forms into the same `WorkflowChoice`. The model sees names/descriptions; transitions alone
see destinations. Implicit `if` options are `yes` and `no` with absent descriptions.

Add one declared-reference lookup in `WorkflowReferences`, shared by semantic validation and planning.
It answers whether `{{inputs.name}}` or `{{stage.output}}` exists and its declared type. Reuse
`WorkflowTemplate`'s parsed references/literal segments rather than parsing template syntax again.

Warning rules, restricted to model questions:

- Warn at `then.ask` for each current-stage `files` output it does not reference.
- Warn at the question for recognizable path-like text outside a complete `files` reference: slash or
  backslash paths, ordinary filename forms such as `README.md`, and composed paths such as
  `{{draft.dir}}/requirements.md`. A suffix appended to a `files` reference does not select a different file.
- Examine source syntax and declarations only. Do not resolve runtime text values, probe the filesystem,
  fetch URLs, or treat HTTP(S) links as local paths. False positives remain advisory.

Keep structural diagnostics and warning validity behavior unchanged. Revise the existing no-context
warning to match the evidence rules. The authoring prompt explains that agents may ignore intentional
warnings and should ask one observable question at a time.

## Evidence preparation

`WorkflowTransitions.Ask` renders the question with existing value semantics. `by: user` branches directly
to the current user flow, without reading files. For a model question it:

1. Resolves only references whose declared type is `Files`, including inputs and earlier outputs.
2. Normalizes their paths, deduplicates normalized paths in first-reference order, and captures outputs
   and the summary with existing user feedback.
3. Captures `question.Confidence ?? liveConfidence` into the plan and enters `Deciding`.

An absent optional input contributes no paths. An unavailable stage output retains the existing pause
for a missing value; that differs from an accepted file subsequently disappearing. Empty file lists
contribute no bodies. Plain-text references still render their values but never cause reads.

Extract the lexical worktree path rule from `WorkflowValues` into `WorkflowFilePaths`, used by initial
validation, planning, and the Core reader. Reject rooted paths and traversal outside the root; normalize
with the platform path APIs and deduplicate normalized relative paths using the existing ordinal policy.
Keep existence/non-empty checks in initial value validation. Path normalization does not read the disk.
Invalid paths during decision preparation become a named user escalation, not an unhandled exception.

`WorkflowFileReader`, constructed with required `IFileSystem` and worktree dependencies, resolves the
path through that helper and calls `TryReadAllText`. Reuse the existing decoder, including its BOM
handling and rejection of invalid text/NULs; do not add an extension list or another decoder. Expected
I/O/access failures return `Unreadable` with a concise reason. The engine supplies the relative path in
the escalation. A file emptied after initial validation contributes its current empty text.

The pending decision reads every selected file before constructing a model request. File bodies are
labelled by normalized relative path and appear only in `Evidence.Files`; rendered questions and output
values retain path labels. A failed read discards prepared partial evidence and produces zero model calls.
Linked files, URLs, unrelated outputs, and conversation history are never expanded.

## Attempt lifecycle and routing

Extract the owned attempt into `WorkflowRunner.PendingDecision.cs`. Keep preparation and inference in
the existing background task, outside `_gate`. Capture its token before scheduling; cancel on retirement
and release the token source after the task has finished using it. Use one synchronized, idempotent
cancel/dispose lifecycle rather than racing token-source disposal against provider registrations.

```text
attempt(plan, token):
  check cancellation
  for path in plan.FilePaths:
    read through the file-reader port
    check cancellation
    if unreadable: return Failed(path + reason)
    collect path + text
  check cancellation
  return await decider.DecideAsync(prepared request, token)

runner.Resolve(attempt, result), under _gate:
  if attempt is no longer _pending: ignore
  apply transitions.Resolve(run, result, attempt.MinimumConfidence)
```

Check cancellation before each read as well as afterward. If `OperationCanceledException` belongs to the
retired attempt, stop quietly; an unexpected cancellation while its token remains active is a failure to
surface, not a permanently stuck decision. Other unexpected exceptions use the existing failure boundary.
Even a provider that ignores cancellation cannot deliver an answer into a successor attempt.

The engine handles results in this order:

| Condition | Outcome |
| --- | --- |
| Refusal, file preparation failure, or inference failure | Ask the user with its reason; do not mislabel file errors as model refusals. |
| Choice not in the decision, missing/invalid confidence, NaN/infinity, or value outside `[0, 1]` | Ask the user with an invalid-answer reason. Never clamp. |
| Confidence below the captured threshold | Ask the same question/choices; report confidence and threshold. |
| Valid answer at or above threshold | Apply existing loop counting/limit, then route. |

Retain loop-limit timing and streak reset rules; this change does not redesign them. Low-confidence and
failed attempts do not count as routed model answers. Human answers bypass model-confidence checks and
retain feedback propagation. Keep the existing question delivery, review-item setting, and run status UI.

No new phase or persisted field is needed. The existing stored definition carries per-decision overrides.
On activation, a saved `Deciding` run starts a fresh attempt, rereading current files and the live setting.
Changing the setting during an active attempt affects the next attempt, not the answer already in flight.

## Settings and inference

Add `SettingKind.Number` with finite `double` values and `SettingsStore.RequireNumber`. Extend the
existing coercion paths: invariant parsing for environment values, numeric TOML values, and JSON numbers
(including numeric strings under the existing tool-call coercion convention). Accept numeric `0` and `1`;
reject nonnumeric and nonfinite values. Write numeric TOML and JSON catalog values, not quoted strings.
The setting's validator supplies the confidence-specific `[0, 1]` range; the numeric kind is reusable.
Preserve existing precedence, invalid-setting reporting, change notifications, and scope behavior.

Register `workflow.decisionConfidence` with default `0.8` and `ApplyMode.Live`. Shared
`HostSession.CreateWorkflowRunner` supplies the reader and accessor for every host. New contracts and
required dependencies change all call sites together; no legacy overloads, capability gates, or adapters.

`ModelWorkflowDecider` asks the decision service one choice question over the prepared request
([inference-providers.md](../inference-providers.md)); the live decision provider returns a probability per label,
and the top label's probability is the confidence. Nothing exposes the threshold or requests tools or prose. A
refusal becomes `Refused`; other failures become `Failed` with their safe detail. Successful values go to the
engine for membership/range checks. Source-generated JSON includes the described choices and file bodies.

Remove `MaxPromptBytes` from `InferenceQueryOptions`, `InferenceService`, workflow/branch/revision callers,
test factories, and affected docs. Keep image/output/time limits and `InputRejected` for other rejected
inputs. Leave the unrelated correction-corpus prompt constant alone. Do not add token estimation,
chunking, truncation, summarization, retries, or another prompt limit.

Claude already sends prompts via stdin; ACP already sends text in `session/prompt`. Preserve those paths.
Provider rejection must remain a failure visible to the caller; for workflows it reaches user escalation.
Use provider error classifications/safe details without embedding raw provider output or file contents in
error messages. No new provider or provider-specific context-limit logic is added.

## Verification strategy

| Seam | Meaningful coverage |
| --- | --- |
| Schema/reader/semantics | Both choice forms, descriptions, thresholds including `0`/`1`, invalid/user overrides, declared-reference lookup, positioned nonblocking warnings and representative path false positives. |
| Transitions | `if`/`then` equality and below-threshold routing; override/global selection; invalid answers/refusal/failure; loop and feedback behavior; typed path selection only. |
| Runner | Controlled read/model barriers prove no model call on any unreadable file, cancellation before/during preparation, stale results after jump/cancel/disposal, active-setting snapshots, and fresh evidence/settings after activation. No timing sleeps. |
| Core reader/adapter | Current file contents, normalized duplicates, BOM/text/binary and I/O failures; prepared JSON with descriptions and no policy values; strict missing confidence and refusal translation. Use injected filesystems and isolated temp files. |
| Settings | Numeric set/list/persist/reload round-trips, precedence/live notifications, finite/range checks, and numeric JSON rather than string serialization. |
| Inference process paths | Multi-megabyte UTF-8 prompts, including non-ASCII content and end markers, traverse service → Claude stdin and service → ACP. Isolated child fakes verify full payload hashes/lengths and return small receipts; account for ACP's appended response schema. Exercise provider rejection separately. |
| Full stack | Real headless app, fake session agent and inference process: model routing, human escalation, file evidence, settings changes, schema/validation tooling, and existing Answer behavior. Run only any transport-specific delta on remote. |

Extend the existing test factories/fakes instead of creating a parallel workflow harness. Keep fake
binaries and settings isolated; no real model calls or shared-PATH replacements. Video captures exercise
the real app with those same deterministic seams. UI-visible outcomes are backed by assertions for
internal invariants such as request contents and zero calls; no production debug setting is needed.

Use focused checks per group and the repository formatters for changed languages. The build workflow
requires fresh-context review, a commit per group, and CI for the full suite. Preserve the approved risks:
reads are not an atomic multi-file snapshot, a synchronous read cannot be interrupted, lexical containment
is not symlink confinement, full evidence uses memory, and utility confidence is not a correctness guarantee.
