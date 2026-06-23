import { For, type JSX, Show, createSignal } from "solid-js";
import { formatKey } from "../commands/keybindings";
import { findCommand, getKeybindings } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ContextMenu, type ContextMenuEntry, type ContextMenuState } from "./ContextMenu";
import type { RailSession } from "./session-store";

// A filled cloud silhouette, reused for the remote marker on a promoted chip and the cloud button glyph.
const CLOUD_PATH = "M6.5 19A4.5 4.5 0 0 1 6 10.05 6 6 0 0 1 17.7 9 4.5 4.5 0 0 1 17.5 19H6.5z";

// The left session rail: the working set — one chip per local session plus any remote sessions the user has
// promoted in. A promoted remote chip wears a small cloud marker (agent-hue at rest, status-coloured when its
// Claude is active), replacing the local status dot. Remote agents themselves live behind the cloud button at
// the bottom (RemoteAgentsPanel), not inline. Right-clicking a chip opens the shared ContextMenu: local chips
// load/unload/delete; a promoted remote offers "Remove from rail". The chip hue is set via a ref using the
// native DOM API (Solid's reactive `style={{}}` binding breaks at runtime here).
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
  // The switch shortcut for the chip at `index` (0-based), read from the resolved keybindings by matching the
  // binding whose index arg is this rail position. Only the first 9 chips have a number binding; the rest get "".
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

  // Right-click menu rows are commands. A local chip targets the session by id (load/unload, delete); the local
  // primary checkout has no worktree to act on, so it opens no menu. A promoted remote chip offers only "Remove
  // from rail" (a working-set action; per-session load/unload/delete route to the active backend and aren't
  // meaningful on a background remote chip — manage those from the cloud panel).
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const menuEntries = (session: RailSession): ContextMenuEntry[] => {
    if (!session.isLocal) {
      return [
        {
          commandId: CommandIds.removeFromRail,
          args: { backendId: session.backendId, id: session.id },
          label: "Remove from rail",
        },
      ];
    }
    const args = { id: session.id };
    return [
      session.loaded
        ? { commandId: CommandIds.unloadSession, args, label: "Unload session" }
        : { commandId: CommandIds.loadSession, args, label: "Load session" },
      { kind: "separator" },
      { commandId: CommandIds.deleteSessionPrompt, args, label: "Delete…", danger: true },
    ];
  };
  const openMenu = (event: MouseEvent, session: RailSession): void => {
    event.preventDefault();
    if (session.isLocal && session.primary) {
      return;
    }
    setMenu({
      x: event.clientX,
      y: event.clientY,
      header: session.isLocal ? session.label : `${session.label} @ ${session.locationName}`,
      entries: menuEntries(session),
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
              }${session.isLocal ? "" : " remote"}`}
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
              {/* Local: a status dot at rest. Remote: the cloud marker takes the dot's place — it says "remote"
                  and carries the same status colour (agent-hue when idle). A dormant chip gets neither. */}
              <Show when={session.loaded}>
                <Show
                  when={session.isLocal}
                  fallback={
                    <span class="session-chip-cloud">
                      <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
                        <path d={CLOUD_PATH} />
                      </svg>
                    </span>
                  }
                >
                  <span class="session-chip-dot" />
                </Show>
              </Show>
            </button>
          )}
        </For>
        {/* The cloud button: opens the remote-agents panel. Only shown once an agent is registered, so a
            solo-local rail stays bare. A pip flags off-rail remote activity (a remote session mid-turn). */}
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
