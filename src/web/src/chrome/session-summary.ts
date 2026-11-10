import type { SessionRun, SessionRunState, SessionStatusName } from "../bridge";
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

/** A run's header tooltip: workflow, stage and state, then its reason on a second line when it has one. */
export function runTooltip(run: SessionRun): string {
  const agent = run.agent === "" ? "" : ` on ${run.agent}`;
  const summary = `${run.workflow} · ${run.stage}${agent} · ${RUN_PHRASE[run.state]}`;
  return run.reason === "" ? summary : `${summary}\n${run.reason}`;
}

/** A loaded session's state, including workflow attention and unseen completed turns. */
function loadedState(session: RailSession): SessionStatusName | "finished" {
  if (session.status === "error" || session.status === "needsInput") {
    return session.status;
  }
  if (session.run !== null) {
    return runStatus(session.run.state);
  }
  return session.status === "idle" && session.unseen ? "finished" : session.status;
}

const STATUS_PHRASE: Record<SessionStatusName | "finished", string> = {
  starting: "starting",
  working: "working",
  needsInput: "needs input",
  idle: "idle",
  finished: "finished",
  waiting: "waiting on a scheduled task",
  error: "crashed",
};

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
    const doing =
      busy && session.activity !== null ? session.activity : RUN_PHRASE[session.run.state];
    return `${session.run.stage} · ${doing}`;
  }
  if (session.loaded && session.workflowProblem !== "") {
    return "workflow run unreadable";
  }
  if (session.loaded && busy && session.activity !== null) {
    return session.activity;
  }
  const loaded = session.loaded ? STATUS_PHRASE[loadedState(session)] : "not loaded";
  const state = session.external ? `outside Weavie · ${loaded}` : loaded;
  const quiet = !session.loaded || session.status === "idle";
  return quiet && session.lastActiveAt !== null
    ? `${state} · ${compactAge(session.lastActiveAt, now)}`
    : state;
}

/** The status-dot modifier: `offline` while its backend is unreachable, `unloaded` when dormant, else its status. */
export function statusDot(session: RailSession): string {
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
