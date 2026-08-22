import { type Accessor, createSignal, type Setter } from "solid-js";
import type { ClientSession } from "../../messaging/host-connection";

export interface AgentPlanDocument {
  id: string;
  markdown: string;
  title: string;
}

interface SessionPlans {
  read: Accessor<Record<string, AgentPlanDocument>>;
  write: Setter<Record<string, AgentPlanDocument>>;
}

const plans = new WeakMap<ClientSession, SessionPlans>();

// Creating on read, not just on write, lets a reader subscribe before the first plan arrives.
function plansFor(session: ClientSession): SessionPlans {
  const existing = plans.get(session);
  if (existing !== undefined) {
    return existing;
  }
  const [read, write] = createSignal<Record<string, AgentPlanDocument>>({});
  const created = { read, write };
  plans.set(session, created);
  return created;
}

// Plans are session-owned documents: an opaque host id selects a stable tab within its owner. The host replays
// their content on reconnect, while the content never enters the filesystem.
export function setAgentPlan(
  session: ClientSession,
  path: string,
  id: string,
  title: string,
  markdown: string,
): string {
  const state = plansFor(session);
  state.write((current) => ({ ...current, [path]: { id, title, markdown } }));
  return path;
}

export function agentPlan(
  session: ClientSession | null,
  path: string,
): AgentPlanDocument | undefined {
  return session === null ? undefined : plansFor(session).read()[path];
}
