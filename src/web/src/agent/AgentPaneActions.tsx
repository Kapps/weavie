import { createMemo, createSignal, For, type JSX, Show } from "solid-js";
import { type AgentInputQuestion, type AgentPaneUpdate, postToHost } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { planIdentity } from "./agent-plan";
import { inputQuestions } from "./input-questions";

export function ApprovalActions(props: {
  slot: string | null;
  requestId: string | null | undefined;
  // The chords answer only the newest pending approval; older cards must not advertise them.
  answersToKeys: boolean;
}): JSX.Element {
  const approve = (decision: string): void => {
    const slot = props.slot;
    const requestId = props.requestId;
    if (slot !== null && requestId !== null && requestId !== undefined && requestId.length > 0) {
      postToHost({ type: "agent-approval", slot, requestId, decision });
    }
  };

  // The mouse path teaches the keyboard path: each decision button wears its command's chord.
  const decision = (label: string, value: string, commandId: string | null): JSX.Element => {
    const key = (): string =>
      props.answersToKeys && commandId !== null ? liveKeyLabel(commandId) : "";
    return (
      <button
        type="button"
        title={key() === "" ? label : `${label} (${key()})`}
        onClick={() => approve(value)}
      >
        {label}
        <Show when={key() !== ""}>
          <kbd class="agent-key-chip">{key()}</kbd>
        </Show>
      </button>
    );
  };

  return (
    <div class="agent-approval-actions">
      {decision("Accept", "accept", CommandIds.agentApprove)}
      {decision("Accept for session", "acceptForSession", CommandIds.agentApproveForSession)}
      {decision("Decline", "decline", CommandIds.agentDecline)}
      {decision("Cancel turn", "cancel", null)}
    </div>
  );
}

export function InputRequestActions(props: {
  slot: string | null;
  message: AgentPaneUpdate;
}): JSX.Element {
  const questions = createMemo(() => inputQuestions(props.message));
  const [answers, setAnswers] = createSignal(defaultAnswers(questions()));

  const submit = (): void => {
    const slot = props.slot;
    const requestId = props.message.itemId;
    if (slot === null || requestId === null || requestId === undefined || requestId.length === 0) {
      return;
    }
    postToHost({ type: "agent-input", slot, requestId, answers: answers() });
  };

  const setAnswer = (id: string, value: string): void => {
    setAnswers({ ...answers(), [id]: value.length === 0 ? [] : [value] });
  };

  return (
    <form
      class="agent-input-request"
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <For each={questions()}>
        {(question) => (
          <label class="agent-input-question">
            <span>{question.header.length > 0 ? question.header : question.question}</span>
            <small>{question.question}</small>
            <Show
              when={question.options.length > 0}
              fallback={
                <input
                  type={question.isSecret ? "password" : "text"}
                  value={answers()[question.id]?.[0] ?? ""}
                  onInput={(event) => setAnswer(question.id, event.currentTarget.value)}
                />
              }
            >
              <select
                value={answers()[question.id]?.[0] ?? ""}
                onChange={(event) => setAnswer(question.id, event.currentTarget.value)}
              >
                <For each={question.options}>
                  {(option) => <option value={option.label}>{option.label}</option>}
                </For>
              </select>
            </Show>
            <Show when={question.options.length > 0}>
              <small>{question.options.map((option) => option.description).join(" ")}</small>
            </Show>
          </label>
        )}
      </For>
      <div class="agent-approval-actions">
        <button type="submit" title="Submit answers (Enter)">
          Submit answers
        </button>
      </div>
    </form>
  );
}

export function EditLocationActions(props: { target: string | null | undefined }): JSX.Element {
  const review = (): void => {
    const location = parseLocation(props.target);
    if (location !== null) {
      postToHost({ type: "reveal-file", path: location.path, line: location.line, preview: true });
    }
  };

  return (
    <div class="agent-approval-actions">
      <button type="button" title="Review edit" onClick={review}>
        Review
      </button>
    </div>
  );
}

export function PlanActions(props: { message: AgentPaneUpdate; slot: string | null }): JSX.Element {
  const identity = (): ReturnType<typeof planIdentity> => planIdentity(props.message);
  const key = (): string => liveKeyLabel(CommandIds.openAgentPlan);
  const open = (): void => {
    const slot = props.slot;
    const plan = identity();
    if (slot === null || plan === null) {
      return;
    }
    void runCommandWithFeedback(CommandIds.openAgentPlan, plan);
  };

  return (
    <Show when={identity()}>
      <div class="agent-approval-actions">
        <button
          type="button"
          title={key() === "" ? "Open plan in editor" : `Open plan in editor (${key()})`}
          onClick={open}
        >
          Open plan
          <Show when={key() !== ""}>
            <kbd class="agent-key-chip">{key()}</kbd>
          </Show>
        </button>
      </div>
    </Show>
  );
}

function defaultAnswers(questions: AgentInputQuestion[]): Record<string, string[]> {
  const answers: Record<string, string[]> = {};
  for (const question of questions) {
    const first = question.options[0]?.label ?? "";
    answers[question.id] = first.length > 0 ? [first] : [];
  }
  return answers;
}

function parseLocation(value: string | null | undefined): { path: string; line: number } | null {
  if (value === null || value === undefined || value.length === 0) {
    return null;
  }
  const split = value.lastIndexOf(":");
  if (split <= 0) {
    return { path: value, line: 1 };
  }
  const line = Number.parseInt(value.slice(split + 1), 10);
  return Number.isFinite(line) && line > 0
    ? { path: value.slice(0, split), line }
    : { path: value, line: 1 };
}
