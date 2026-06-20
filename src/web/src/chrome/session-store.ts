import { createSignal } from "solid-js";
import { type SessionChip, type SessionStatusName, onHostMessage } from "../bridge";

// The window's session rail + the active session's Claude status — the two pieces of host-pushed session
// state the chrome shows. Both are TOP-LEVEL module signals (the same trick layout/store.ts and
// editor/session-store.ts use), seeded by the host's pushes and NOT reloaded when App hot-swaps, so they
// survive HMR. The host only pushes session-list / session-status on `ready` (a full page load) and on
// each later change — never on an HMR, which doesn't re-post `ready`. So a *component* signal would reset
// to empty on every hot reload and stay empty until the next incidental change, blanking the rail. Keeping
// the state here means it just carries across the swap. They MUST therefore be imported at top level (by
// App.tsx), not only reached through a hot-swapping component, or they'd reload with it and lose state.
//
// The listener is registered at module load — which runs before main.tsx sends "ready" — so the host's
// first push can never race ahead of it.
const [list, setList] = createSignal<SessionChip[]>([]);
const [status, setStatus] = createSignal<SessionStatusName | undefined>(undefined);

onHostMessage((message) => {
  if (message.type === "session-list") {
    setList(message.sessions);
  } else if (message.type === "session-status" && message.session === "claude") {
    setStatus(message.status);
  }
});

/** The window's sessions for the left rail, or [] until the host's first session-list push. */
export const sessions = list;

/** The active session's Claude status for the pane-head dot, or undefined until the first push. */
export const claudeStatus = status;
