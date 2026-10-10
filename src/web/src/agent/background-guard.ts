import { createSignal } from "solid-js";
import type { CommandResult } from "../commands/types";

/** One running item a stopping action would end, as the host lists it. */
export interface BackgroundWorkSummary {
  name: string;
  type: string;
  state: string;
  startedAtMs: number;
}

interface StopRequest {
  work: BackgroundWorkSummary[];
  settle: (stop: boolean) => void;
}

const [request, setRequest] = createSignal<StopRequest | null>(null);

/** The pending "Stop background work?" question, if any. */
export const backgroundStopRequest = request;

/** Asks whether to stop `work`; resolves true for Close anyway, false for Keep session. */
export function confirmStopBackgroundWork(work: BackgroundWorkSummary[]): Promise<boolean> {
  request()?.settle(false);
  return new Promise((resolve) =>
    setRequest({
      work,
      settle: (stop) => {
        setRequest(null);
        resolve(stop);
      },
    }),
  );
}

/** The running work a host refusal lists, or null when the result is not a background-work refusal. */
export function backgroundWorkOf(result: CommandResult): BackgroundWorkSummary[] | null {
  const work = (result.data as { backgroundWork?: unknown } | null | undefined)?.backgroundWork;
  return !result.ok && Array.isArray(work) ? (work as BackgroundWorkSummary[]) : null;
}

/** Turns a host refusal into the confirm, re-running the command with consent when the user closes anyway. */
export async function guardBackgroundWork(
  result: Promise<CommandResult>,
  args: unknown,
  run: (args: unknown) => Promise<CommandResult>,
): Promise<CommandResult> {
  const first = await result;
  const work = backgroundWorkOf(first);
  if (work === null) return first;
  if (!(await confirmStopBackgroundWork(work))) return { ok: false };
  return run({ ...(args as object | undefined), stopBackgroundWork: true });
}
