import { For, type JSX, Show, createSignal } from "solid-js";
import type { SessionChip } from "../bridge";
import { formatKey } from "../commands/keybindings";
import { findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ContextMenu, type ContextMenuEntry, type ContextMenuState } from "./ContextMenu";

// The left session rail: one chip per session (a deterministic hashed hue + the branch monogram), with a
// status dot and an active-session accent, plus a "+" to spin up a new worktree session. The host orders the
// chips loaded-first, dormant (unloaded) ones last; a dormant chip renders faded. Right-clicking a non-primary
// chip opens the shared command-driven ContextMenu to load/unload or delete it; the primary checkout has no
// separate worktree, so it has no menu. Always rendered: the host always has at least the primary session and
// pushes the list on init, and the "+" is how the user discovers sessions in the first place. The chip hue is
// set via a ref using the native DOM API (Solid's reactive `style={{}}` binding is avoided on purpose — it
// breaks at runtime in this app; see the project notes).
export function SessionRail(props: {
  sessions: SessionChip[];
  onSwitch: (id: string) => void;
  onNew: () => void;
}): JSX.Element {
  // Advertise the New Session shortcut on the "+" tooltip, read from the command catalog (never hardcoded),
  // so the keyboard path is discoverable from the mouse. Unbound / no-catalog (plain-browser dev) → bare label.
  const newTitle = (): string => {
    const keys = findCommand(CommandIds.newSessionPrompt)?.keys ?? [];
    return keys.length > 0 ? `New session (${keys.map(formatKey).join(" / ")})` : "New session";
  };

  // Right-click menu, built for the right-clicked chip and rendered by the shared ContextMenu. Its rows are
  // commands targeting the chip by id (load/unload, delete). The primary checkout has no worktree to act on,
  // so it opens no menu.
  const [menu, setMenu] = createSignal<ContextMenuState | null>(null);
  const menuEntries = (session: SessionChip): ContextMenuEntry[] => {
    const args = { id: session.id };
    return [
      session.loaded
        ? { commandId: CommandIds.unloadSession, args, label: "Unload session" }
        : { commandId: CommandIds.loadSession, args, label: "Load session" },
      { kind: "separator" },
      { commandId: CommandIds.deleteSessionPrompt, args, label: "Delete…", danger: true },
    ];
  };
  const openMenu = (event: MouseEvent, session: SessionChip): void => {
    event.preventDefault();
    if (session.primary) {
      return;
    }
    setMenu({
      x: event.clientX,
      y: event.clientY,
      header: session.label,
      entries: menuEntries(session),
    });
  };

  return (
    <>
      <div class="session-rail">
        <For each={props.sessions}>
          {(session) => (
            <button
              type="button"
              class={`session-chip status-${session.status}${session.active ? " active" : ""}${
                session.loaded ? "" : " unloaded"
              }`}
              title={
                session.loaded
                  ? `${session.label} — ${session.status}`
                  : `${session.label} — unloaded (click to load)`
              }
              ref={(el) => el.style.setProperty("--chip-hue", String(session.hue))}
              onClick={() => props.onSwitch(session.id)}
              onContextMenu={(event) => openMenu(event, session)}
            >
              <span class="session-chip-mono">{session.monogram}</span>
              {/* A dormant chip has no live Claude, so no status dot — the faded chip is the indicator. */}
              <Show when={session.loaded}>
                <span class="session-chip-dot" />
              </Show>
            </button>
          )}
        </For>
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
