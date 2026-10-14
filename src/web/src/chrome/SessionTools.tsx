import { ChevronDown, ChevronRight, Pin, PinOff } from "lucide-solid";
import { createSignal, type JSX, Show } from "solid-js";
import { liveKeyHint, liveKeyLabel } from "../commands/keys-live";
import { captureCommandRunner } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { panelPinned, parkedCollapsed, setParkedCollapsed } from "./rail-state";
import { sessionMenuAt } from "./session-menu";
import type { RailSession } from "./session-store";
import { CLOUD_PATH, newSessionTitle, sessionSubtitle, statusDot } from "./session-summary";

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
export function ParkedToggle(props: { class: string; count: number; label: string }): JSX.Element {
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
      {props.label}
    </button>
  );
}

/** The session panel's shortcut and its pin button: in the pinned panel's header, or at the expanded rail's foot. */
export function PanelControls(props: { onTogglePin: () => void }): JSX.Element {
  return (
    <>
      <Show when={liveKeyLabel(CommandIds.toggleSessionPanel)}>
        {(keys) => <span class="session-panel-key">{keys()}</span>}
      </Show>
      <button
        type="button"
        class="session-panel-pin"
        classList={{ on: panelPinned() }}
        title={`${panelPinned() ? "Unpin" : "Pin"} session panel${liveKeyHint(CommandIds.pinSessionPanel)}`}
        aria-pressed={panelPinned()}
        onClick={() => props.onTogglePin()}
      >
        <Show when={panelPinned()} fallback={<Pin size={13} />}>
          <PinOff size={13} />
        </Show>
      </button>
    </>
  );
}

/** A session's full name and what its agent is doing: a pinned panel row or an expanded chip. */
export function SessionText(props: { session: RailSession; now: number }): JSX.Element {
  return (
    <span class="session-panel-text">
      <span class="session-panel-title">{props.session.title}</span>
      <span class="session-panel-activity">{sessionSubtitle(props.session, props.now)}</span>
    </span>
  );
}

/** A session's status dot; a remote session's status reads as a cloud in its place. Both share one slot, so names align. */
export function SessionStatus(props: { session: RailSession }): JSX.Element {
  return (
    <span class="session-status-slot">
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
    </span>
  );
}
