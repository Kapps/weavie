# Workflow decision evidence — requirements

Scope: [approved brief](brief.md). Each numbered story has EARS-style acceptance criteria.

## 1. Meaningful confidence

As a workflow user, I want decisions to express uncertainty before advancing the run.

- When a model answers, Weavie shall receive an allowed choice and finite numeric confidence in `[0, 1]`,
  or a refusal/failure, without requiring generated reasoning.
- When the utility model answers, it shall supply numeric confidence rather than assigning every ordinary
  choice `1.0`. The workflow engine shall have no implicit `unsure` answer; any such interpretation shall
  remain inside the adapter.

## 2. Configurable thresholds

As a workflow author, I want to control how confident an automatic decision must be.

- Where no decision override exists, Weavie shall use the live `workflow.decisionConfidence` setting,
  defaulting to `0.8`. A changed setting shall apply to subsequent decisions without restarting the run.
- Where a model `if` or `then` declares `confidence:`, Weavie shall use that override. When a valid answer
  meets or exceeds the threshold, it shall route normally, subject to the existing loop limit.
- If a threshold is nonnumeric, nonfinite, or outside `[0, 1]`, validation shall reject it. If a `by: user`
  decision declares a threshold, workflow validation shall reject it.

## 3. Human escalation

As a workflow user, I want to decide when the model cannot provide a usable answer.

- If confidence is below threshold, the model refuses, the provider fails, or the response contains an
  invalid choice or confidence, Weavie shall ask the user without advancing or clamping the result.
- When escalating, Weavie shall preserve the question and choices and state the reason; low-confidence
  reasons shall include the reported confidence and effective threshold.
- When a decision explicitly requires the user or reaches its loop limit, Weavie shall still ask the user.
  User answers, feedback, question presentation, and review-item opening shall retain their existing behavior.

## 4. Referenced file contents

As a workflow author, I want decisions to judge documents supplied through typed file references.

- When a model `ask` references a `files` input or output, Weavie shall include each file's complete current
  text as evidence separate from the question. Current-stage and earlier-stage outputs shall both work.
- When gathering evidence, Weavie shall resolve paths within the owning session's worktree, identify each
  file by path, and include its contents once even if referenced repeatedly.
- When deciding whether a file is readable text, Weavie shall use its contents rather than its extension.
  Existing validation of `files` inputs and outputs shall still apply.

## 5. Unreadable evidence

As a workflow user, I want to know when a decision cannot examine all its referenced evidence.

- If any referenced file is binary, missing, or unreadable at decision time, Weavie shall ask the user,
  identify the offending file and reason, and make no model call for that decision.
- When readable and unreadable files are referenced together, Weavie shall escalate the whole question
  rather than submit partial evidence. This shall also apply to files that become unavailable after validation.

## 6. Explicit context

As a workflow author, I want to know exactly what evidence a decision receives.

- When a model decides, Weavie shall supply its question, referenced values and file contents, stage output
  values, and completion summary under the existing context rules, including feedback propagation.
- Unless `ask` explicitly references a `files` value, Weavie shall not read file contents. Literal paths,
  paths assembled from text values, unreferenced file outputs, URLs, and document links shall not trigger reads.
- When constructing decision context, Weavie shall not add the conversation transcript, scan the workspace,
  fetch linked resources, or discover additional evidence.

## 7. Provider-owned prompt limits

As a typed-inference user, I want complete prompts to reach my provider and provider failures to be visible.

- When any caller submits typed inference, Weavie shall impose no prompt-byte cap and shall expose no
  `MaxPromptBytes` option. It shall not truncate, summarize, or split the prompt to fit a replacement limit.
- When prompts substantially exceed the removed limits, both terminal-Claude stdin and ACP shall receive
  the complete prompt intact.
- If the provider rejects the prompt, Weavie shall surface that failure to the caller; workflow decisions
  shall escalate it to the user. Other inference limits shall remain outside this change.

## 8. Choice descriptions

As a workflow author, I want to explain what distinguishes each named choice.

- Where a choice declares a plain-text `description`, Weavie shall provide it to the decider with its label.
- When choices use either `approve: build` or `approve: { then: build, description: "…" }`, Weavie shall
  accept and route both forms, including mixtures. Omitting a description shall remain valid.

## 9. Advisory evidence warnings

As a workflow author, I want likely evidence mistakes flagged without blocking intentional choices.

- When a stage declares a `files` output that its model `then.ask` does not reference, validation shall warn
  that its contents will not be read.
- When a model `if` or `then` question contains path-like text without a `files` reference, including paths
  assembled from ordinary text references, validation shall warn about the missing file evidence.
- Where only warnings exist, the workflow shall remain valid, saveable, and runnable. Authors and agents
  shall be free to ignore warnings after judgment; structural errors shall remain blocking errors.

## 10. Accurate authoring guidance

As an authoring agent or workflow user, I want guidance that matches the decision's actual capabilities.

- When presenting authoring instructions, schema guidance, or `docs/specs/workflows.md`, Weavie shall
  describe the confidence rules, optional choice descriptions, explicit evidence boundary, and user escalation.
- When advising authors, the guidance shall recommend observable criteria and one concern per question,
  explain that warnings require judgment, and allow intentional warnings to remain unresolved.
- The guidance shall not claim that `unsure` is an implicit engine answer or that referenced file contents
  are unavailable to model decisions.

Excluded: new decision providers, score questions, batching, new history/UI, multimedia interpretation,
OCR, model tools, automatic evidence discovery, and backward-compatibility machinery.
