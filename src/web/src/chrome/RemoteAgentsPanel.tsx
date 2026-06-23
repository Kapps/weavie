import { For, type JSX, Show, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import type { RailSession, RemoteAgentRow } from "./session-store";

// The cloud panel: manage + pick remote agents, opened from the rail's cloud button. Lists each agent
// (connected first, offline faded) and its sessions; clicking a session promotes it into the rail and
// switches to it. Anchored above the cloud button, dismissed on outside-click / Escape.
export function RemoteAgentsPanel(props: {
  agents: RemoteAgentRow[];
  anchor: { left: number; bottom: number };
  isPromoted: (backendId: string, id: string) => boolean;
  onPick: (session: RailSession) => void;
  onDisconnect: (name: string) => void;
  onAddRemote: () => void;
  onClose: () => void;
}): JSX.Element {
  const onPointerDown = (event: PointerEvent): void => {
    // Ignore the cloud button too, so clicking it to close doesn't close-then-reopen via its toggle handler.
    if (!(event.target as HTMLElement).closest(".remote-panel, .session-rail-cloud")) {
      props.onClose();
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      props.onClose();
    }
  };
  onMount(() => {
    window.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("keydown", onKeyDown);
  });
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("keydown", onKeyDown);
  });

  // An agent's avatar monogram: the first two letters of its name, uppercased.
  const monogram = (name: string): string => name.slice(0, 2).toUpperCase();

  return (
    <Portal>
      <div
        class="remote-panel"
        ref={(el) => {
          el.style.left = `${props.anchor.left}px`;
          el.style.bottom = `${props.anchor.bottom}px`;
        }}
      >
        <div class="remote-panel-head">Remote agents</div>
        <For each={props.agents}>
          {(agent) => (
            <div class={`remote-agent${agent.connected ? "" : " offline"}`}>
              <div class="remote-agent-head">
                <span
                  class="remote-agent-ava"
                  ref={(el) => el.style.setProperty("--chip-hue", String(agent.hue))}
                >
                  {monogram(agent.name)}
                </span>
                <span class="remote-agent-name" title={agent.name}>
                  {agent.name}
                </span>
                <Show when={!agent.connected}>
                  <span class="remote-agent-state">offline</span>
                </Show>
                <button
                  type="button"
                  class="remote-agent-x"
                  title={`Disconnect ${agent.name}`}
                  onClick={() => props.onDisconnect(agent.name)}
                >
                  ✕
                </button>
              </div>
              <Show when={agent.connected && agent.sessions.length > 0}>
                <div class="remote-agent-sessions">
                  <For each={agent.sessions}>
                    {(session) => (
                      <button
                        type="button"
                        class={`remote-session status-${session.status}${
                          props.isPromoted(session.backendId, session.id) ? " in-rail" : ""
                        }${session.loaded ? "" : " unloaded"}`}
                        title={`${session.label}${session.loaded ? ` — ${session.status}` : " — unloaded"}`}
                        ref={(el) => el.style.setProperty("--chip-hue", String(session.hue))}
                        onClick={() => props.onPick(session)}
                      >
                        <span class="remote-session-mono">{session.monogram}</span>
                        <Show when={session.loaded && session.status !== "idle"}>
                          <span class="remote-session-dot" />
                        </Show>
                      </button>
                    )}
                  </For>
                </div>
              </Show>
            </div>
          )}
        </For>
        <button type="button" class="remote-panel-add" onClick={() => props.onAddRemote()}>
          ＋ Add remote agent…
        </button>
      </div>
    </Portal>
  );
}
