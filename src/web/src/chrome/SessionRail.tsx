import { createSignal, For, type JSX, Show } from "solid-js";
import { formatKey } from "../commands/keybindings";
import { captureCommandRunner, findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { cancelSessionPanelHover, hoverRail } from "./SessionPanel";
import { sessionMenuAt } from "./session-menu";
import type { RailSession } from "./session-store";
import { sessionSubtitle, statusDot, switchShortcut } from "./session-summary";

// A filled cloud silhouette for the remote-agents button.
const CLOUD_PATH = "M6.5 19A4.5 4.5 0 0 1 6 10.05 6 6 0 0 1 17.7 9 4.5 4.5 0 0 1 17.5 19H6.5z";

// The left session rail (working set): one status dot per local session plus promoted remotes, loaded first and
// parked after a divider. Names live in the session panel, which opens on hover; remote agents live behind the
// cloud button (RemoteAgentsPanel).
export function SessionRail(props: {
  sessions: RailSession[];
  inert: boolean;
  hasRemotes: boolean;
  remoteActive: boolean;
  onSwitch: (session: RailSession) => void;
  onNew: () => void;
  onToggleRemotes: (anchor: DOMRect) => void;
}): JSX.Element {
  // Advertise the New Session shortcut on the "+" tooltip, read from the command catalog (never hardcoded).
  const newTitle = (): string => {
    const keys = findCommand(CommandIds.showSessions)?.keys ?? [];
    return keys.length > 0 ? `New session (${keys.map(formatKey).join(" / ")})` : "New session";
  };
  const chipTitle = (session: RailSession, index: number): string => {
    const where = session.isLocal ? "" : ` @ ${session.locationName}`;
    const shortcut = switchShortcut(index);
    const base = `${session.label}${where} — ${sessionSubtitle(session, Date.now())}`;
    return shortcut !== "" ? `${base} (${shortcut})` : base;
  };
  const firstParked = (): number => props.sessions.findIndex((session) => !session.loaded);

  // Right-click menu rows are commands (see session-menu.ts). Load/unload + delete for every session, plus
  // "Remove from rail" for a remote.
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const openMenu = (event: MouseEvent, session: RailSession): void => {
    event.preventDefault();
    setMenu(sessionMenuAt(session, event.clientX, event.clientY, true, captureCommandRunner()));
  };

  return (
    <>
      <div
        class="session-rail"
        inert={props.inert}
        onPointerEnter={() => hoverRail(true)}
        onPointerLeave={() => hoverRail(false)}
        onPointerDown={cancelSessionPanelHover}
      >
        <For each={props.sessions}>
          {(session, index) => (
            <>
              <Show when={index() > 0 && index() === firstParked()}>
                <div class="session-rail-divider" />
              </Show>
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
              </button>
            </>
          )}
        </For>
        {/* The cloud button opens the remote-agents panel; shown only once an agent is registered. A pip
            flags off-rail remote activity. */}
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
          title={newTitle()}
          onClick={() => props.onNew()}
        >
          +
        </button>
      </div>
      <Show when={menu()}>{(m) => <ContextMenu menu={m()} onClose={() => setMenu(null)} />}</Show>
    </>
  );
}
