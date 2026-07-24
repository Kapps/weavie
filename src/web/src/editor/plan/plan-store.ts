import { createSignal } from "solid-js";

export interface AgentPlanDocument {
  id: string;
  markdown: string;
  title: string;
}

const [plans, setPlans] = createSignal<Record<string, AgentPlanDocument>>({});
const pathsById = new Map<string, string>();
let nextPath = 0;

// Plans are transient documents: their opaque host id selects a stable tab for this web session, while their
// content never enters the persisted editor session or the filesystem.
export function setAgentPlan(id: string, title: string, markdown: string): string {
  let path = pathsById.get(id);
  if (path === undefined) {
    path = `agent-plan:${++nextPath}`;
    pathsById.set(id, path);
  }
  setPlans((current) => ({ ...current, [path]: { id, title, markdown } }));
  return path;
}

export function agentPlan(path: string): AgentPlanDocument | undefined {
  return plans()[path];
}
