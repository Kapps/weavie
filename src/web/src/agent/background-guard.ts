import { type Accessor, createSignal } from "solid-js";
import { CommandIds, type CommandResult } from "../commands/types";
import type { SessionAddress } from "../messaging/message-envelope";
import { type AgentBackgroundItem, runningBackgroundOf } from "./agent-background-store";

/** How the user answered: stop the work, keep it, or it finished while they looked. */
export type StopChoice = "stop" | "keep" | "finished";

/** One running item as a host refusal lists it. */
export interface BackgroundWorkSummary {
  name: string;
  type: string;
  state: string;
  startedAtMs: number;
}

interface StopRequest {
  confirmLabel: string;
  /** What the host refused over, shown until the live list has caught up with it. */
  refused: BackgroundWorkSummary[];
  /** The work still running now; null once its session is gone. */
  running: Accessor<AgentBackgroundItem[] | null>;
  settle: (choice: StopChoice) => void;
}

const [request, setRequest] = createSignal<StopRequest | null>(null);

/** The pending "Stop background work?" question, if any. */
export const backgroundStopRequest = request;

// The confirm button names what proceeding does; only these actions stop background work.
const confirmLabels = new Map<string, string>([
  [CommandIds.unloadSession, "Stop and unload"],
  [CommandIds.deleteSession, "Stop and delete"],
  [CommandIds.recreateSession, "Stop and recreate"],
  [CommandIds.restartAgent, "Stop and restart"],
  [CommandIds.clearAgentConversation, "Stop and clear"],
  [CommandIds.rewindAgentConversation, "Stop and rewind"],
  [CommandIds.exit, "Stop and exit"],
]);

/** Asks whether the action `commandId` may stop the `refused` work, following it live through `running`. */
export function confirmStopBackgroundWork(
  commandId: string,
  refused: BackgroundWorkSummary[],
  running: Accessor<AgentBackgroundItem[] | null>,
): Promise<StopChoice> {
  const confirmLabel = confirmLabels.get(commandId);
  if (confirmLabel === undefined) throw new Error(`${commandId} does not stop background work.`);
  request()?.settle("keep");
  return new Promise((resolve) =>
    setRequest({
      confirmLabel,
      refused,
      running,
      settle: (choice) => {
        setRequest(null);
        resolve(choice);
      },
    }),
  );
}

/** Whether the confirm has nothing left to ask: its session is gone, or the live list caught up and emptied. */
export function stopQuestionMoot(
  running: AgentBackgroundItem[] | null,
  caughtUp: boolean,
): boolean {
  return running === null || (caughtUp && running.length === 0);
}

interface BackgroundRefusal {
  session: SessionAddress;
  backgroundWork: BackgroundWorkSummary[];
}

/** The session and work a host refusal names, or null when the result is not a background-work refusal. */
export function backgroundRefusal(result: CommandResult): BackgroundRefusal | null {
  const data = result.data as Partial<BackgroundRefusal> | undefined;
  return !result.ok && data?.session !== undefined && Array.isArray(data.backgroundWork)
    ? (data as BackgroundRefusal)
    : null;
}

/**
 * Turns a host refusal into the confirm, which follows the refused session's work live. Stop re-runs with consent;
 * work that finishes meanwhile re-runs without it, so anything started since is asked about again.
 */
export async function guardBackgroundWork(
  commandId: string,
  backendId: string,
  result: Promise<CommandResult>,
  args: unknown,
  run: (args: unknown) => Promise<CommandResult>,
): Promise<CommandResult> {
  const first = await result;
  const refusal = backgroundRefusal(first);
  if (refusal === null) return first;
  const choice = await confirmStopBackgroundWork(commandId, refusal.backgroundWork, () =>
    runningBackgroundOf(backendId, refusal.session),
  );
  if (choice === "keep") return { ok: false, cancelled: true };
  if (choice === "finished") return guardBackgroundWork(commandId, backendId, run(args), args, run);
  return run({ ...(args as object | undefined), stopBackgroundWork: true });
}
