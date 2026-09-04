import { type Accessor, createSignal, type Setter } from "solid-js";
import type { ClientSession } from "../../messaging/host-connection";
import { createSessionOwnedState } from "../../messaging/session-owned-state";

export interface AgentPlanDocument {
  id: string;
  markdown: string;
  title: string;
}

interface SessionPlans {
  read: Accessor<Record<string, AgentPlanDocument>>;
  write: Setter<Record<string, AgentPlanDocument>>;
}

const plans = createSessionOwnedState<SessionPlans>(() => {
  const [read, write] = createSignal<Record<string, AgentPlanDocument>>({});
  return { read, write };
});

// Plans are session-owned documents: an opaque host id selects a stable tab within its owner. The host replays
// their content on reconnect, while the content never enters the filesystem.
export function setAgentPlan(
  session: ClientSession,
  path: string,
  id: string,
  title: string,
  markdown: string,
): string {
  const state = plans.get(session)!;
  state.write((current) => ({ ...current, [path]: { id, title, markdown } }));
  return path;
}

export function agentPlan(
  session: ClientSession | null,
  path: string,
): AgentPlanDocument | undefined {
  return plans.get(session)?.read()[path];
}
