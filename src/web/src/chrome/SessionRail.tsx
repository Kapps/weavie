import { For, type JSX, Show, createSignal } from "solid-js";
import { formatKey } from "../commands/keybindings";
import { findCommand, getKeybindings } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ContextMenu, type ContextMenuState } from "./ContextMenu";
import { sessionMenuEntries } from "./session-menu";
import type { RailSession } from "./session-store";

// A filled cloud silhouette, reused for the remote marker on a promoted chip and the cloud button glyph.
const CLOUD_PATH = "M6.5 19A4.5 4.5 0 0 1 6 10.05 6 6 0 0 1 17.7 9 4.5 4.5 0 0 1 17.5 19H6.5z";

// The left session rail (working set): one chip per local session plus promoted remotes. A promoted remote
// chip wears a cloud marker in place of the local status dot; remote agents themselves live behind the cloud
// button (RemoteAgentsPanel). Chip hue is set via a ref because Solid's reactive `style={{}}` breaks here.
export function SessionRail(props: {
  sessions: RailSession[];
  hasRemotes: boolean;
  remoteActive: boolean;
  onSwitch: (session: RailSession) => void;
  onNew: () => void;
  onToggleRemotes: (anchor: DOMRect) => void;
}): JSX.Element {
  // Advertise the New Session shortcut on the "+" tooltip, read from the command catalog (never hardcoded).
  const newTitle = (): string => {
    const keys = findCommand(CommandIds.newSessionPrompt)?.keys ?? [];
    return keys.length > 0 ? `New session (${keys.map(formatKey).join(" / ")})` : "New session";
  };
  // The switch shortcut for the chip at `index` (0-based); only the first 9 chips have a number binding.
  const switchShortcut = (index: number): string => {
    const match = getKeybindings().find(
      (binding) =>
        binding.command === CommandIds.selectSessionByIndex &&
        (binding.args as { index?: unknown } | undefined)?.index === index + 1,
    );
    return match !== undefined ? formatKey(match.key) : "";
  };
  // The chip's hover tooltip: its label + status (or unloaded hint), with the switch shortcut appended.
  const chipTitle = (session: RailSession, index: number): string => {
    const where = session.isLocal ? "" : ` @ ${session.locationName}`;
    const base = session.loaded
      ? `${session.label}${where} — ${session.status}`
      : `${session.label}${where} — unloaded (click to load)`;
    const shortcut = switchShortcut(index);
    return shortcut !== "" ? `${base} (${shortcut})` : base;
  };

  // Right-click menu rows are commands (see session-menu.ts). Load/unload + delete for any session, plus
  // "Remove from rail" for a remote; the primary checkout has no worktree, so it opens no menu.
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const openMenu = (event: MouseEvent, session: RailSession): void => {
    event.preventDefault();
    if (session.isLocal && session.primary) {
      return;
    }
    setMenu({
      x: event.clientX,
      y: event.clientY,
      header: session.isLocal ? session.label : `${session.label} @ ${session.locationName}`,
      entries: sessionMenuEntries(session, true),
    });
  };

  return (
    <>
      <div class="session-rail">
        <For each={props.sessions}>
          {(session, index) => (
            <button
              type="button"
              class={`session-chip status-${session.status}${session.active ? " active" : ""}${
                session.loaded ? "" : " unloaded"
              }${session.isLocal ? "" : " remote"}${session.pending ? " pending" : ""}`}
              title={chipTitle(session, index())}
              ref={(el) => {
                el.style.setProperty("--chip-hue", String(session.hue));
                if (session.agentHue !== undefined) {
                  el.style.setProperty("--agent-hue", String(session.agentHue));
                }
              }}
              onClick={() => props.onSwitch(session)}
              onContextMenu={(event) => openMenu(event, session)}
            >
              <span class="session-chip-mono">{session.monogram}</span>
              {/* A host op in flight (delete / load / unload): a spinner overlays the chip, replacing the
                  status marker, until it settles. Otherwise — local: a status dot; remote: a cloud marker in
                  the dot's place (agent-hue when idle); a dormant chip gets neither. */}
              <Show when={session.pending}>
                <span class="session-chip-spinner" aria-hidden="true" />
              </Show>
              <Show when={!session.pending && session.loaded && session.isLocal}>
                <span class="session-chip-dot" />
              </Show>
              <Show when={!session.pending && session.loaded && !session.isLocal}>
                <span class="session-chip-cloud">
                  <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                    <path d={CLOUD_PATH} />
                  </svg>
                </span>
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
