import { createSignal } from "solid-js";
import { onHostMessage, postToHost } from "../bridge";
import type { EditorSession } from "./session-types";

// The latest editor session: seeded by the host's set-editor-session push at page load, then kept live by
// the editor host via setLocalSession. The listener is registered at module load — which runs before
// main.tsx sends "ready" — so the host's reply can never race ahead of it. Crucially this is a TOP-LEVEL
// module signal that is NOT reloaded when the App or the editor chunk hot-swaps, so it survives HMR: the
// editor host re-reads it on re-create and restores the exact live position (this is the same trick that
// keeps layout/store.ts alive across HMR). It MUST therefore be imported at top level (by App.tsx), not
// only through the dynamically-imported editor chunk, or it would reload with that chunk and lose state.
const [session, setSession] = createSignal<EditorSession | null>(null);

onHostMessage((message) => {
  if (message.type === "set-editor-session") {
    setSession(message.session);
  }
});

/// The most recent editor session (host launch push, or the live local state), or null until one arrives.
export const editorSession = session;

// The host persist is debounced: the cursor/scroll hooks fire rapidly and the host only needs the settled
// state (mirrors the layout store's debounced layout-changed).
let postTimer: ReturnType<typeof setTimeout> | undefined;

/// Records the live editor session locally (so a hot reload restores the exact current position) AND posts
/// a debounced editor-session-changed to the host to persist. Never sends file content — disk is the truth.
export function setLocalSession(next: EditorSession): void {
  setSession(next);
  if (postTimer !== undefined) {
    clearTimeout(postTimer);
  }
  postTimer = setTimeout(() => {
    postTimer = undefined;
    // Strip any content before persisting (the host never trusts the web for file contents).
    const open = next.open.map((entry) => ({
      path: entry.path,
      viewState: entry.viewState ?? null,
    }));
    postToHost({ type: "editor-session-changed", session: { active: next.active, open } });
  }, 300);
}
