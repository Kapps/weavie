import { type Accessor, createSignal, type Setter } from "solid-js";
import type { AgentInputQuestion, AgentPaneUpdate, ClientSession } from "../bridge";
import { createSessionOwnedMap } from "../messaging/session-owned-state";
import { paneItemIdentity } from "./AgentPaneIdentity";

export type AgentInputAnswers = Record<string, string[]>;

interface AgentInputDraft {
  answers: Accessor<AgentInputAnswers>;
  setAnswers: Setter<AgentInputAnswers>;
}

const drafts = createSessionOwnedMap<string, AgentInputDraft>();

export function agentInputRequestKey(message: AgentPaneUpdate): string {
  return paneItemIdentity(message) ?? message.itemId ?? "";
}

export function agentInputDraft(
  session: ClientSession,
  requestKey: string,
  questions: readonly AgentInputQuestion[],
): AgentInputDraft {
  let draft = drafts.get(session, requestKey);
  if (draft === undefined) {
    const [answers, setAnswers] = createSignal(defaultAnswers(questions));
    draft = { answers, setAnswers };
    drafts.set(session, requestKey, draft);
  }
  return draft;
}

export function clearAgentInputDrafts(session: ClientSession): void {
  drafts.clear(session);
}

export function clearAgentInputDraft(session: ClientSession, requestKey: string): void {
  drafts.delete(session, requestKey);
}

function defaultAnswers(questions: readonly AgentInputQuestion[]): AgentInputAnswers {
  const answers: AgentInputAnswers = {};
  for (const question of questions) {
    if (question.initialValues.length > 0) {
      answers[question.id] = [...question.initialValues];
    } else if (question.kind === "boolean") {
      answers[question.id] = ["false"];
    } else {
      answers[question.id] = [];
    }
  }
  return answers;
}
