import { ChevronDown, ChevronRight } from "lucide-solid";
import { createSignal, type JSX, Show } from "solid-js";
import { liveKeyHint } from "../commands/keys-live";
import { captureCommandRunner } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { parkedCollapsed, setParkedCollapsed } from "./rail-state";
import { sessionMenuAt } from "./session-menu";
import type { RailSession } from "./session-store";
import { CLOUD_PATH, newSessionTitle, statusDot } from "./session-summary";

/** The new-session and remote-agents buttons: at the rail's foot, or in the pinned session panel's header. */
export function SessionTools(props: {
  hasRemotes: boolean;
  onNew: () => void;
  onToggleRemotes: (anchor: DOMRect) => void;
}): JSX.Element {
  return (
    <>
      {/* Shown once a remote agent is registered. */}
      <Show when={props.hasRemotes}>
        <button
          type="button"
          class="session-rail-cloud"
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
      setMenu(sessionMenuAt(session, event.clientX, event.clientY, captureCommandRunner()));
    },
    View: () => (
      <Show when={menu()}>{(m) => <ContextMenu menu={m()} onClose={() => setMenu(null)} />}</Show>
    ),
  };
}

/** Folds or unfolds parked (unloaded) sessions; one shared state for the rail and the session panel. */
export function ParkedToggle(props: { class: string; count: number; label?: string }): JSX.Element {
  return (
    <button
      type="button"
      class={props.class}
      aria-expanded={!parkedCollapsed()}
      title={`${parkedCollapsed() ? "Show" : "Hide"} ${props.count} parked session${props.count === 1 ? "" : "s"}${liveKeyHint(CommandIds.toggleParkedSessions)}`}
      onClick={() => setParkedCollapsed(!parkedCollapsed())}
    >
      <Show when={parkedCollapsed()} fallback={<ChevronDown size={11} />}>
        <ChevronRight size={11} />
      </Show>
      {props.label ?? props.count}
    </button>
  );
}

/** A session's status dot; a remote session's status reads as a cloud in its place. */
export function SessionStatus(props: { session: RailSession }): JSX.Element {
  return (
    <Show
      when={props.session.isLocal}
      fallback={
        <svg
          class={`session-status cloud status-${statusDot(props.session)}`}
          viewBox="0 0 24 24"
          aria-hidden="true"
        >
          <path d={CLOUD_PATH} />
        </svg>
      }
    >
      <span class={`session-status status-${statusDot(props.session)}`} />
    </Show>
  );
}
