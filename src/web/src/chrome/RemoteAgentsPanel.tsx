import { For, type JSX, Show, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { sessionMenuEntries } from "./session-menu";
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
    // Ignore the cloud button (so its toggle handler isn't fought) and our own context menu — it portals
    // OUTSIDE the panel, so without this a mousedown on a menu item closes the panel, unmounting the menu
    // (a child of this component) mid-click, and the click lands on whatever was behind it.
    if (
      !(event.target as HTMLElement).closest(".remote-panel, .session-rail-cloud, .context-menu")
    ) {
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

  // Right-click a remote session: the same command-driven menu as the rail (load/unload + delete, routed to
  // the owning backend), plus "Remove from rail" once it's promoted. Without this, the right-click fell
  // through to the WebView's own context menu.
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const openMenu = (event: MouseEvent, session: RailSession): void => {
    event.preventDefault();
    setMenu({
      x: event.clientX,
      y: event.clientY,
      header: `${session.label} @ ${session.locationName}`,
      entries: sessionMenuEntries(session, props.isPromoted(session.backendId, session.id)),
    });
  };

  return (
    <>
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
                          }${session.loaded ? "" : " unloaded"}${session.pending ? " pending" : ""}`}
                          title={`${session.label}${session.loaded ? ` — ${session.status}` : " — unloaded"}`}
                          ref={(el) => el.style.setProperty("--chip-hue", String(session.hue))}
                          onClick={() => props.onPick(session)}
                          onContextMenu={(event) => openMenu(event, session)}
                        >
                          <span class="remote-session-mono">{session.monogram}</span>
                          <Show
                            when={session.pending}
                            fallback={
                              <Show when={session.loaded && session.status !== "idle"}>
                                <span class="remote-session-dot" />
                              </Show>
                            }
                          >
                            <span class="session-chip-spinner" aria-hidden="true" />
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
      <Show when={menu()}>{(m) => <ContextMenu menu={m()} onClose={() => setMenu(null)} />}</Show>
    </>
  );
}
