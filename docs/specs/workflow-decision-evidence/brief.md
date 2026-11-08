# Workflow decision evidence

Let workflow decisions judge explicit evidence, including file contents, and ask the user when the model
is uncertain. Keep the question separate from its evidence so dedicated decision providers can fit later.

- **Confidence:** deciders return an allowed choice plus finite confidence in `[0, 1]`, or refusal/failure.
  The utility model supplies numeric confidence; ordinary answers are not assigned `1.0`. The engine has
  no implicit `unsure` answer; any such handling stays inside the adapter. Add live
  `workflow.decisionConfidence` (default `0.8`) and optional `confidence:` overrides on model `if` and
  `then` decisions. Equality passes. Invalid thresholds, including overrides on `by: user`, fail validation.
  Low confidence, refusal, invalid responses, or provider errors ask the user with the same question and
  choices and a concrete reason. Existing user approvals, feedback, and loop limits still apply.

- **File evidence:** when a model's `ask` references a `files` input or output, include its complete current
  text as evidence. This includes earlier stages' outputs. Resolve paths within the session worktree,
  identify each file by path, and include it once. Use the existing content-based text reader. If any file
  is binary, missing, or unreadable at decision time, name it and ask the user without calling the model
  on partial evidence. Initial `files` validation remains in place.

- **Context:** decisions see their question, referenced values/contents, stage output values, and summary
  (including existing feedback). Only explicit `files` references trigger reads. Literal paths, paths
  assembled from text values, unreferenced file outputs, URLs, and links inside documents do not. No
  transcript, workspace scan, or other evidence is automatically included. User questions retain their
  existing presentation and review-item opening behavior.

- **Prompt size:** remove `MaxPromptBytes` from typed-inference options, validation, and every caller.
  Send full prompts without replacement caps, truncation, or summarization; provider rejection must reach
  the caller and workflow user escalation. Verify large prompts reach both terminal-Claude stdin and ACP
  intact using deterministic process fakes. Other inference limits and unrelated same-name constants are
  outside scope.

- **Choices:** add optional descriptions and pass them to the decider. Keep concise targets alongside
  described targets as supported authoring forms:

  ```yaml
  choices:
    approve:
      then: build
      description: The document defines observable acceptance criteria.
    revise: draft
  ```

- **Authoring:** warn when a stage's model `then.ask` omits its `files` outputs, or a model `if`/`then`
  question contains path-like text without a `files` reference. Warnings never block validation, saving,
  or execution; agents may ignore them after judgment. Update `author-workflow`, schema guidance, and
  `docs/specs/workflows.md` to describe the evidence boundary and confidence behavior, and encourage
  observable questions addressing one concern each.

Verify threshold boundaries and overrides, refusal/error escalation, file inclusion and deduplication,
zero model calls for unreadable evidence, intact large prompts on both transports, both choice forms,
and nonblocking warnings. Detailed requirements, architecture, and test plans belong in later stages.

Out of scope: Jev/OpenAI providers, score questions, batching, new history/UI, multimedia interpretation,
OCR, model tools, automatic evidence discovery, and backward-compatibility machinery.

Provider reference: shared evidence is OpenAI `input` / Jev `state`; choice/score answers expose probabilities
and confidence, while predicate/noul answers expose a probability that future adapters must normalize.
Confidence is not a correctness guarantee. Provider context limits (including Jev's 32k state-plus-question
context) are provider-owned. Descriptions are optional in Weavie; Jev also documents null descriptions.
See [OpenAI Decisions](https://developers.openai.com/api/reference/resources/decisions/methods/create),
[Jev confidence](https://docs.typesafe.ai/confidence), and [Jev Choice](https://docs.typesafe.ai/primitives/choice).
