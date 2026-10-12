import { X } from "lucide-solid";
import { For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { registerFloatingPanel } from "./floating-panels";
import { type PopoverAnchor, placeRailPopover } from "./popover-position";
import type { RemoteAgentRow } from "./session-store";

// The cloud panel: add and disconnect remote agents, opened from the cloud button. Lists each agent (connected
// first, offline faded); their sessions are on the rail. Placement stays beside the cloud button and within the
// viewport; outside-click / Escape dismisses it.
export function RemoteAgentsPanel(props: {
  agents: RemoteAgentRow[];
  anchor: PopoverAnchor;
  onDisconnect: (name: string) => void;
  onAddRemote: () => void;
  onClose: () => void;
}): JSX.Element {
  let panel!: HTMLDivElement;
  let panelObserver!: ResizeObserver;
  const positionPanel = (): void => {
    const bounds = panel.getBoundingClientRect();
    const position = placeRailPopover(
      props.anchor,
      { width: bounds.width, height: bounds.height },
      { width: window.innerWidth, height: window.innerHeight },
    );
    panel.style.left = `${position.left}px`;
    panel.style.top = `${position.top}px`;
    panel.style.visibility = "visible";
  };
  const onPointerDown = (event: PointerEvent): void => {
    // Ignore the cloud button, so its own toggle handler isn't fought.
    if (!(event.target as HTMLElement).closest(".remote-panel, .session-rail-cloud")) {
      props.onClose();
    }
  };
  onMount(() => {
    window.addEventListener("pointerdown", onPointerDown);
    onCleanup(registerFloatingPanel("remote-agents", props.onClose, "popover").dispose);
    window.addEventListener("resize", positionPanel);
    panelObserver = new ResizeObserver(positionPanel);
    panelObserver.observe(panel);
    positionPanel();
  });
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("resize", positionPanel);
    panelObserver.disconnect();
  });

  // An agent's avatar monogram: the first two letters of its name, uppercased.
  const monogram = (name: string): string => name.slice(0, 2).toUpperCase();

  return (
    <Portal>
      <div class="remote-panel" ref={panel}>
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
                  aria-label={`Disconnect ${agent.name}`}
                  onClick={() => props.onDisconnect(agent.name)}
                >
                  <X />
                </button>
              </div>
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
