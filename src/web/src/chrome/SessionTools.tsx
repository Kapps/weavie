import { createSignal, type JSX, Show } from "solid-js";
import { captureCommandRunner } from "../commands/registry";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { sessionMenuAt } from "./session-menu";
import type { RailSession } from "./session-store";
import { CLOUD_PATH, newSessionTitle } from "./session-summary";

/** The new-session and remote-agents buttons: at the rail's foot, or in the pinned session panel's header. */
export function SessionTools(props: {
  hasRemotes: boolean;
  remoteActive: boolean;
  onNew: () => void;
  onToggleRemotes: (anchor: DOMRect) => void;
}): JSX.Element {
  return (
    <>
      {/* Shown once a remote agent is registered; a pip flags remote activity off the rail. */}
      <Show when={props.hasRemotes}>
        <button
          type="button"
          class={`session-rail-cloud${props.remoteActive ? " active" : ""}`}
          title="Remote agents"
          onClick={(event) => props.onToggleRemotes(event.currentTarget.getBoundingClientRect())}
        >
          <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path d={CLOUD_PATH} />
          </svg>
        </button>
      </Show>
      <button
        type="button"
        class="session-rail-add"
        title={newSessionTitle()}
        onClick={() => props.onNew()}
      >
        +
      </button>
    </>
  );
}

/** A session's right-click menu (load/unload, delete, remove from rail), shared by the rail and the panel. */
export function createSessionMenu(): {
  open: (event: MouseEvent, session: RailSession) => void;
  View: () => JSX.Element;
} {
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  return {
    open: (event, session) => {
      event.preventDefault();
      setMenu(sessionMenuAt(session, event.clientX, event.clientY, true, captureCommandRunner()));
    },
    View: () => (
      <Show when={menu()}>{(m) => <ContextMenu menu={m()} onClose={() => setMenu(null)} />}</Show>
    ),
  };
}
