import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature } from "../bridge";

/** One subagent or background task a structured agent runs beside its turns. */
export interface AgentBackgroundItem {
  id: string;
  kind: "subagent" | "task";
  name: string;
  type: string;
  detail: string | null;
  lastActivity: string | null;
  usage: { totalTokens: number | null; toolUses: number | null; durationMs: number | null } | null;
  state: "running" | "paused" | "completed" | "failed" | "stopped";
  canStop: boolean;
  startedAtMs: number;
  endedAtMs: number | null;
  transcriptItemId: string | null;
}

const [bySession, setBySession] = createSignal(new Map<ClientSession, AgentBackgroundItem[]>());

registerSessionFeature((session) => {
  const stop = session
    .feature("agent")
    .on<{ items: AgentBackgroundItem[] }>("background", ({ items }) => {
      setBySession(new Map(bySession()).set(session, items));
    });
  return () => {
    stop();
    const next = new Map(bySession());
    next.delete(session);
    setBySession(next);
  };
});

/** One exact session's background work: live items, plus finished ones until its next prompt. */
export function agentBackground(session: ClientSession | null): AgentBackgroundItem[] {
  return (session === null ? undefined : bySession().get(session)) ?? [];
}

/** Every live session's running background work, for actions that stop them all. */
export function runningBackgroundEverywhere(): AgentBackgroundItem[] {
  return [...bySession().values()].flat().filter(backgroundRunning);
}

export function backgroundRunning(item: AgentBackgroundItem): boolean {
  return item.state === "running" || item.state === "paused";
}
