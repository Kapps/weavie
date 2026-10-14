import { formatKey } from "../commands/keybindings";
import { findCommand, getKeybindings } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { compactAge } from "../compact-age";
import type { RailSession } from "./session-store";

/** The session panel's sections, in display order. */
export type SessionGroup = "needs" | "working" | "idle" | "parked";

export const SESSION_GROUPS: { id: SessionGroup; title: string }[] = [
  { id: "needs", title: "Needs you" },
  { id: "working", title: "Working" },
  { id: "idle", title: "Idle" },
  { id: "parked", title: "Parked" },
];

/** Which panel section a session belongs in. */
export function sessionGroup(session: RailSession): SessionGroup {
  if (!session.loaded) {
    return "parked";
  }
  switch (session.status) {
    case "needsInput":
    case "error":
      return "needs";
    case "idle":
      return "idle";
    default:
      return "working";
  }
}

/** `sessions` in the session panel's display order: grouped by SESSION_GROUPS, rail order within a group. */
export function panelOrder(sessions: RailSession[]): RailSession[] {
  const rank = (session: RailSession): number =>
    SESSION_GROUPS.findIndex((group) => group.id === sessionGroup(session));
  return [...sessions].sort((a, b) => rank(a) - rank(b));
}

const STATUS_PHRASE: Record<RailSession["status"], string> = {
  starting: "starting",
  working: "working",
  needsInput: "needs input",
  idle: "idle",
  waiting: "waiting on a scheduled task",
  error: "crashed",
};

/** The line under a session's name: what its agent is doing, else its state and how long ago it was active. */
export function sessionSubtitle(session: RailSession, now: number): string {
  if (session.offline) {
    return "connection lost";
  }
  // Status arrives before the coalesced activity, so a line is shown only while it can still be current.
  const busy = session.status === "working" || session.status === "needsInput";
  if (session.loaded && busy && session.activity !== null) {
    return session.activity;
  }
  const state = session.loaded ? STATUS_PHRASE[session.status] : "not loaded";
  const quiet = !session.loaded || session.status === "idle";
  return quiet && session.lastActiveAt !== null
    ? `${state} · ${compactAge(session.lastActiveAt, now)}`
    : state;
}

/** The status-dot modifier: `offline` while its backend is unreachable, `unloaded` when dormant, else its status. */
export function statusDot(session: RailSession): string {
  return session.offline ? "offline" : session.loaded ? session.status : "unloaded";
}

/** The switch shortcut for the rail session at `index` (0-based); only the first nine have one. */
export function switchShortcut(index: number): string {
  const match = getKeybindings().find(
    (binding) =>
      binding.command === CommandIds.selectSessionByIndex &&
      (binding.args as { index?: unknown } | undefined)?.index === index + 1,
  );
  return match !== undefined ? formatKey(match.key) : "";
}

/** A filled cloud silhouette: the remote-agents button and the remote marker on a session. */
export const CLOUD_PATH =
  "M6.5 19A4.5 4.5 0 0 1 6 10.05 6 6 0 0 1 17.7 9 4.5 4.5 0 0 1 17.5 19H6.5z";

/** The new-session button's tooltip, advertising its shortcut from the command catalog. */
export function newSessionTitle(): string {
  const keys = findCommand(CommandIds.showSessions)?.keys ?? [];
  return keys.length > 0 ? `New session (${keys.map(formatKey).join(" / ")})` : "New session";
}
