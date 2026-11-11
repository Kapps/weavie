import type { SessionRunState, SessionStatusName } from "../bridge";
import { formatKey } from "../commands/keybindings";
import { liveIndexedKeyLabel } from "../commands/keys-live";
import { findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { compactAge } from "../compact-age";
import type { RailSession } from "./session-store";

/** The session panel's sections, in display order. */
export type SessionGroup = "needs" | "working" | "finished" | "idle" | "parked";

export const SESSION_GROUPS: { id: SessionGroup; title: string }[] = [
  { id: "needs", title: "Needs you" },
  { id: "working", title: "Working" },
  { id: "finished", title: "Finished" },
  { id: "idle", title: "Idle" },
  { id: "parked", title: "Parked" },
];

/** Which panel section a session belongs in. */
export function sessionGroup(session: RailSession): SessionGroup {
  if (!session.loaded) {
    return "parked";
  }
  if (session.reloadRequired !== null) {
    return "needs";
  }
  switch (loadedState(session)) {
    case "needsInput":
    case "error":
      return "needs";
    case "finished":
      return "finished";
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

const RUN_PHRASE: Record<SessionRunState, string> = {
  working: "working",
  waitingOnYou: "waiting on you",
  needsAnswer: "needs your answer",
  watching: "watching in background",
  deciding: "deciding",
  paused: "paused",
  stalled: "stalled",
};

const RUN_STATUS: Record<SessionRunState, SessionStatusName> = {
  working: "working",
  waitingOnYou: "needsInput",
  needsAnswer: "needsInput",
  watching: "waiting",
  deciding: "working",
  paused: "needsInput",
  stalled: "error",
};

/** A workflow's status, shared by the session indicators and its pane header. */
export function runStatus(state: SessionRunState): SessionStatusName {
  return RUN_STATUS[state];
}

/** A loaded session's state, including workflow attention and unseen completed turns. */
function loadedState(session: RailSession): SessionStatusName | "finished" {
  // A pending workflow question or pause needs attention once the agent rests, not during a follow-up.
  if (session.status !== "idle") {
    return session.status;
  }
  if (session.run !== null) {
    return runStatus(session.run.state);
  }
  return session.unseen ? "finished" : "idle";
}

export type SessionDisplayStatus = SessionStatusName | "finished" | "offline" | "unloaded";

const STATUS_LABEL: Record<SessionDisplayStatus, string> = {
  starting: "Starting",
  working: "Working",
  needsInput: "Needs input",
  idle: "Idle",
  finished: "Finished",
  waiting: "Background work",
  error: "Error",
  offline: "Connection lost",
  unloaded: "Not loaded",
};

/** A short, provider-neutral label for the session's effective status. */
export function sessionStatusLabel(session: RailSession): string {
  return session.reloadRequired !== null && !session.offline
    ? "Needs reload"
    : STATUS_LABEL[statusDot(session)];
}

function sessionStateText(session: RailSession): string {
  return session.loaded &&
    !session.offline &&
    session.reloadRequired === null &&
    session.run !== null &&
    loadedState(session) === runStatus(session.run.state)
    ? RUN_PHRASE[session.run.state]
    : sessionStatusLabel(session).toLowerCase();
}

/** The session's state, with workflow context and the full attention reason when present. */
export function sessionTooltip(session: RailSession): string {
  const run = session.loaded ? session.run : null;
  const agent = run === null || run.agent === "" ? "" : ` on ${run.agent}`;
  const context = run === null ? "" : `${run.workflow} · ${run.stage}${agent} · `;
  const summary = `${context}${sessionStateText(session)}`;
  const reason = sessionDetail(session);
  return reason === "" ? summary : `${summary}\n${reason}`;
}

/** Why a session's workflow needs the user, in full (the subtitle stays short), or empty. */
export function sessionDetail(session: RailSession): string {
  if (!session.loaded) {
    return "";
  }
  return session.reloadRequired ?? (session.run?.reason || session.workflowProblem);
}

/** The line under a session's name: its run's stage, else what its agent is doing, else its state and age. */
export function sessionSubtitle(session: RailSession, now: number): string {
  if (session.offline) {
    return "connection lost";
  }
  if (session.reloadRequired !== null) {
    return "needs reload";
  }
  // Status arrives before the coalesced activity, so a line is shown only while it can still be current.
  const busy = session.status === "working" || session.status === "needsInput";
  if (session.loaded && session.run !== null) {
    const doing = busy && session.activity !== null ? session.activity : sessionStateText(session);
    return `${session.run.stage} · ${doing}`;
  }
  if (session.loaded && session.workflowProblem !== "") {
    return "workflow run unreadable";
  }
  if (session.loaded && busy && session.activity !== null) {
    return session.activity;
  }
  const loaded = sessionStateText(session);
  const state = session.external ? `outside Weavie · ${loaded}` : loaded;
  const quiet = !session.loaded || session.status === "idle";
  return quiet && session.lastActiveAt !== null
    ? `${state} · ${compactAge(session.lastActiveAt, now)}`
    : state;
}

/** The status-dot modifier: `offline` while its backend is unreachable, `unloaded` when dormant, else its status. */
export function statusDot(session: RailSession): SessionDisplayStatus {
  if (session.offline) {
    return "offline";
  }
  if (session.reloadRequired !== null) {
    return "error";
  }
  return session.loaded ? loadedState(session) : "unloaded";
}

/** The switch shortcut for the rail session at `index` (0-based); only the first nine have one. */
export function switchShortcut(index: number): string {
  return liveIndexedKeyLabel(CommandIds.selectSessionByIndex, index + 1);
}

/** A filled cloud silhouette: the remote-agents button and the remote marker on a session. */
export const CLOUD_PATH =
  "M6.5 19A4.5 4.5 0 0 1 6 10.05 6 6 0 0 1 17.7 9 4.5 4.5 0 0 1 17.5 19H6.5z";

/** The new-session button's tooltip, advertising its shortcut from the command catalog. */
export function newSessionTitle(): string {
  const keys = findCommand(CommandIds.showSessions)?.keys ?? [];
  return keys.length > 0 ? `New session (${keys.map(formatKey).join(" / ")})` : "New session";
}
