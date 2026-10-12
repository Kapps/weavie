import { createMemo, For, type JSX, Show } from "solid-js";
import { panelPinned, parkedCollapsed } from "./rail-state";
import { createSessionMenu, ParkedToggle, SessionTools } from "./SessionTools";
import { cancelSessionPanelHover } from "./session-panel-hover";
import type { RailSession } from "./session-store";
import { CLOUD_PATH, sessionSubtitle, statusDot, switchShortcut } from "./session-summary";

// The left session rail: every session on every connected backend as a status dot over the first words of its
// name, loaded first and parked (unloaded) ones folded behind a toggle. The full session panel opens on hover.
export function SessionRail(props: {
  sessions: RailSession[];
  inert: boolean;
  hasRemotes: boolean;
  onSwitch: (session: RailSession) => void;
  onNew: () => void;
  onToggleRemotes: (anchor: DOMRect) => void;
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

  return (
    <>
      {/* Pinned, the session panel takes the rail's place. */}
      <Show when={!panelPinned()}>
        <div class="session-rail" inert={props.inert} onPointerDown={cancelSessionPanelHover}>
          <For each={props.sessions}>
            {(session, index) => (
              <>
                <Show when={index() === firstParked()}>
                  <ParkedToggle class="session-rail-parked" count={parked()} />
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
                    ref={(el) => {
                      if (session.agentHue !== undefined) {
                        el.style.setProperty("--agent-hue", String(session.agentHue));
                      }
                    }}
                    onClick={() => props.onSwitch(session)}
                    onContextMenu={(event) => menu.open(event, session)}
                  >
                    {/* A host op in flight (delete / load / unload) spins over the dot until it settles. */}
                    <span class={`session-status status-${statusDot(session)}`} />
                    <Show when={session.pending}>
                      <span class="session-chip-spinner" aria-hidden="true" />
                    </Show>
                    <span class="session-chip-words">
                      {session.title.split(" ").slice(0, 2).join(" ")}
                    </span>
                    <Show when={!session.isLocal}>
                      <svg
                        class="session-chip-cloud"
                        viewBox="0 0 24 24"
                        fill="currentColor"
                        aria-hidden="true"
                      >
                        <path d={CLOUD_PATH} />
                      </svg>
                    </Show>
                  </button>
                </Show>
              </>
            )}
          </For>
          <SessionTools
            hasRemotes={props.hasRemotes}
            onNew={props.onNew}
            onToggleRemotes={props.onToggleRemotes}
          />
        </div>
      </Show>
      <menu.View />
    </>
  );
}
