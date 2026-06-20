import { For, type JSX, Show } from "solid-js";
import type { SessionChip } from "../bridge";
import { formatKey } from "../commands/keybindings";
import { findCommand, getKeybindings } from "../commands/registry";
import { CommandIds } from "../commands/types";

// The left session rail: one chip per session (a deterministic hashed hue + the branch monogram), with a
// status dot and an active-session accent, plus a "+" to spin up a new worktree session. Always rendered:
// the host always has at least the primary session and pushes the list on init, and the "+" is how the
// user discovers sessions in the first place — so there's no "no sessions" state to hide behind. The chip
// hue is set via a ref using the native DOM API (Solid's reactive `style={{}}` binding is avoided on
// purpose — it breaks at runtime in this app; see the project notes).
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
  // The Ctrl+Shift+<number> switch shortcut for the chip at `index` (0-based), read from the resolved
  // keybindings (never hardcoded) by matching the binding whose index arg is this rail position. Only the
  // first 9 chips have a number binding; the rest (and plain-browser dev with no catalog) get "".
  const switchShortcut = (index: number): string => {
    const match = getKeybindings().find(
      (binding) =>
        binding.command === CommandIds.selectSessionByIndex &&
        (binding.args as { index?: unknown } | undefined)?.index === index + 1,
    );
    return match !== undefined ? formatKey(match.key) : "";
  };
  // The chip's hover tooltip: its label + status (or unloaded hint), with the switch shortcut appended so
  // the keyboard path is discoverable from the mouse.
  const chipTitle = (session: SessionChip, index: number): string => {
    const base = session.loaded
      ? `${session.label} — ${session.status}`
      : `${session.label} — unloaded (click to load)`;
    const shortcut = switchShortcut(index);
    return shortcut !== "" ? `${base} (${shortcut})` : base;
  };
  return (
    <div class="session-rail">
      <For each={props.sessions}>
        {(session, index) => (
          <button
            type="button"
            class={`session-chip status-${session.status}${session.active ? " active" : ""}${
              session.loaded ? "" : " unloaded"
            }`}
            title={chipTitle(session, index())}
            ref={(el) => el.style.setProperty("--chip-hue", String(session.hue))}
            onClick={() => props.onSwitch(session.id)}
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
  );
}
