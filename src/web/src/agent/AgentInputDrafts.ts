import { type Accessor, createSignal, type Setter } from "solid-js";
import type { AgentInputQuestion, AgentPaneUpdate, ClientSession } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";

export type AgentInputAnswers = Record<string, string[]>;

interface AgentInputDraft {
  answers: Accessor<AgentInputAnswers>;
  setAnswers: Setter<AgentInputAnswers>;
}

const drafts = new WeakMap<ClientSession, Map<string, AgentInputDraft>>();

export function agentInputRequestKey(message: AgentPaneUpdate): string {
  return paneItemIdentity(message) ?? message.itemId ?? "";
}

export function agentInputDraft(
  session: ClientSession,
  requestKey: string,
  questions: readonly AgentInputQuestion[],
): AgentInputDraft {
  let sessionDrafts = drafts.get(session);
  if (sessionDrafts === undefined) {
    sessionDrafts = new Map();
    drafts.set(session, sessionDrafts);
  }

  let draft = sessionDrafts.get(requestKey);
  if (draft === undefined) {
    const [answers, setAnswers] = createSignal(defaultAnswers(questions));
    draft = { answers, setAnswers };
    sessionDrafts.set(requestKey, draft);
  }
  return draft;
}

export function clearAgentInputDrafts(session: ClientSession): void {
  drafts.delete(session);
}

export function clearAgentInputDraft(session: ClientSession, requestKey: string): void {
  const sessionDrafts = drafts.get(session);
  sessionDrafts?.delete(requestKey);
  if (sessionDrafts?.size === 0) {
    drafts.delete(session);
  }
}

function defaultAnswers(questions: readonly AgentInputQuestion[]): AgentInputAnswers {
  const answers: AgentInputAnswers = {};
  for (const question of questions) {
    const first = question.options[0]?.label ?? "";
    answers[question.id] = first.length > 0 ? [first] : [];
  }
  return answers;
}
