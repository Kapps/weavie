import { createSignal, For, type JSX, Show } from "solid-js";
import { captureCommandRunner } from "../commands/registry";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { panelPinned } from "./rail-state";
import { sessionMenuAt } from "./session-menu";
import { cancelSessionPanelHover } from "./session-panel-hover";
import type { RailSession } from "./session-store";
import {
  CLOUD_PATH,
  newSessionTitle,
  sessionSubtitle,
  statusDot,
  switchShortcut,
} from "./session-summary";

// The left session rail (working set): each local session and promoted remote as a status dot over the first words
// of its name, loaded first. The full session panel opens on hover; remote agents live behind the cloud button.
export function SessionRail(props: {
  sessions: RailSession[];
  inert: boolean;
  hasRemotes: boolean;
  remoteActive: boolean;
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

  // Right-click menu rows are commands (see session-menu.ts). Load/unload + delete for every session, plus
  // "Remove from rail" for a remote.
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const openMenu = (event: MouseEvent, session: RailSession): void => {
    event.preventDefault();
    setMenu(sessionMenuAt(session, event.clientX, event.clientY, true, captureCommandRunner()));
  };

  return (
    <>
      {/* Pinned, the session panel takes the rail's place. */}
      <Show when={!panelPinned()}>
        <div class="session-rail" inert={props.inert} onPointerDown={cancelSessionPanelHover}>
          <For each={props.sessions}>
            {(session, index) => (
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
                onContextMenu={(event) => openMenu(event, session)}
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
            )}
          </For>
          {/* The cloud button opens the remote-agents panel; shown only once an agent is registered. A pip
            flags off-rail remote activity. */}
          <Show when={props.hasRemotes}>
            <button
              type="button"
              class={`session-rail-cloud${props.remoteActive ? " active" : ""}`}
              title="Remote agents"
              onClick={(event) =>
                props.onToggleRemotes(event.currentTarget.getBoundingClientRect())
              }
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
        </div>
      </Show>
      <Show when={menu()}>{(m) => <ContextMenu menu={m()} onClose={() => setMenu(null)} />}</Show>
    </>
  );
}
