import { createMemo, For, type JSX, Show } from "solid-js";
import { floatingPanelLevel } from "./floating-panels";
import { parkedCollapsed } from "./rail-state";
import {
  createSessionMenu,
  PanelControls,
  ParkedToggle,
  SessionText,
  SessionTools,
} from "./SessionTools";
import { cancelSessionPanelHover } from "./session-panel-hover";
import type { RailSession } from "./session-store";
import { CLOUD_PATH, sessionSubtitle, statusDot, switchShortcut } from "./session-summary";

/** The session panel's list behavior, applied to the rail while it stands in for the unpinned panel. */
export interface SessionListHandlers {
  ref: (element: HTMLElement) => void;
  onKeyDown: (event: KeyboardEvent) => void;
  onFocusOut: (event: FocusEvent) => void;
}

// The left session rail: every session on every connected backend as a status dot (a cloud for remotes) over the first words of its
// name, loaded first and parked (unloaded) ones folded behind a toggle. Expanded (the unpinned session panel), it widens in place and
// each chip gains its full name and activity beside its dot, so nothing moves vertically.
export function SessionRail(props: {
  sessions: RailSession[];
  inert: boolean;
  hasRemotes: boolean;
  expanded: boolean;
  now: number;
  list: SessionListHandlers;
  onSwitch: (session: RailSession) => void;
  onNew: () => void;
  onToggleRemotes: (anchor: DOMRect) => void;
  onTogglePin: () => void;
}): JSX.Element {
  const chipTitle = (session: RailSession, index: number): string => {
    const where = session.isLocal ? "" : ` @ ${session.locationName}`;
    const shortcut = switchShortcut(index);
    const base = `${session.label}${where} — ${sessionSubtitle(session, Date.now())}`;
    return shortcut !== "" ? `${base} (${shortcut})` : base;
  };

  const menu = createSessionMenu();
  const parked = createMemo(() => props.sessions.filter((session) => !session.loaded).length);
  const firstParked = createMemo(() => props.sessions.findIndex((session) => !session.loaded));
  // The tools open their own surfaces, so they dismiss a hover-expanded rail.
  return (
    <div class="session-rail-slot">
      <nav
        {...props.list}
        class="session-rail"
        classList={{ expanded: props.expanded }}
        style={props.expanded ? `z-index:${floatingPanelLevel("session-panel")}` : undefined}
        inert={props.inert}
        aria-label="Sessions"
      >
        <For each={props.sessions}>
          {(session, index) => (
            <>
              <Show when={index() === firstParked()}>
                <ParkedToggle
                  class="session-rail-parked"
                  count={parked()}
                  label={props.expanded ? `Parked · ${parked()}` : String(parked())}
                />
              </Show>
              <Show when={session.loaded || !parkedCollapsed()}>
                <button
                  type="button"
                  class={`session-chip status-${session.status}${session.active ? " active" : ""}${
                    session.loaded ? "" : " unloaded"
                  }${session.isLocal ? "" : " remote"}${session.pending ? " pending" : ""}${
                    session.offline ? " offline" : ""
                  }`}
                  data-session-slot={session.id}
                  title={chipTitle(session, index())}
                  // A pointer click leaves the pointer on the rail, so hover must not reopen it there.
                  onClick={(event) => {
                    if (event.detail > 0) cancelSessionPanelHover();
                    props.onSwitch(session);
                  }}
                  onContextMenu={(event) => menu.open(event, session)}
                >
                  <span class="session-chip-face">
                    {/* A host op in flight (delete / load / unload) spins over the dot until it settles. */}
                    <Show
                      when={session.isLocal}
                      fallback={
                        <svg
                          class={`session-status cloud status-${statusDot(session)}`}
                          viewBox="0 0 24 24"
                          aria-hidden="true"
                        >
                          <path d={CLOUD_PATH} />
                        </svg>
                      }
                    >
                      <span class={`session-status status-${statusDot(session)}`} />
                    </Show>
                    <Show when={session.pending}>
                      <span class="session-chip-spinner" aria-hidden="true" />
                    </Show>
                    <span class="session-chip-words">
                      {session.title.split(" ").slice(0, 2).join(" ")}
                    </span>
                  </span>
                  {/* Out of flow, so expanding never changes the chip's height. */}
                  <Show when={props.expanded}>
                    <span class="session-chip-detail">
                      <span class="session-panel-num">{index() < 9 ? index() + 1 : ""}</span>
                      <SessionText session={session} now={props.now} />
                    </span>
                  </Show>
                </button>
              </Show>
            </>
          )}
        </For>
        <div class="session-rail-foot">
          {/* The tools open their own surfaces, so they dismiss a hover-expanded rail. */}
          <div class="session-rail-tools" onPointerDown={cancelSessionPanelHover}>
            <SessionTools
              hasRemotes={props.hasRemotes}
              onNew={props.onNew}
              onToggleRemotes={props.onToggleRemotes}
            />
          </div>
          <Show when={props.expanded}>
            <PanelControls onTogglePin={props.onTogglePin} />
          </Show>
        </div>
      </nav>
      <menu.View />
    </div>
  );
}
