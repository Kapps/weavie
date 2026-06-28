// Resolves keydowns against the resolved keybindings via one capture-phase listener (so a chord wins over a
// focused xterm/Monaco), preventDefaulting only when a binding matches AND its handler consumes the event.
// Single-chord matching only; multi-stroke sequences are not handled. See docs/specs/commands.md.

import { evaluateWhen } from "./context";
import { getKeybindings, onCommandsChanged, runForKeybinding } from "./registry";
import type { ResolvedKeybinding } from "./types";

/** Whether the runtime is macOS — used to resolve `$mod` and platform-specific key handling. */
export const IS_MAC =
  /Mac|iP(hone|ad|od)/.test(navigator.platform) || /Mac/.test(navigator.userAgent);

interface Chord {
  mod: boolean;
  ctrl: boolean;
  shift: boolean;
  alt: boolean;
  meta: boolean;
  key: string;
}

// Maps each shifted punctuation glyph back to its base key. KeyboardEvent.key reports the shifted glyph
// ("}", not "]"), but specs use the base char and match Shift separately, so without this a Shift+symbol
// binding could never match. Letters fold via toLowerCase instead.
const SHIFTED_TO_BASE: Record<string, string> = {
  "~": "`",
  "!": "1",
  "@": "2",
  "#": "3",
  $: "4",
  "%": "5",
  "^": "6",
  "&": "7",
  "*": "8",
  "(": "9",
  ")": "0",
  _: "-",
  "+": "=",
  "{": "[",
  "}": "]",
  "|": "\\",
  ":": ";",
  '"': "'",
  "<": ",",
  ">": ".",
  "?": "/",
};

// Folds KeyboardEvent.key's long names ("ArrowUp", "Escape", " ") and spec tokens ("Up", "Esc", "Space")
// onto one canonical token so e.g. "$mod+Up" matches a real Ctrl+ArrowUp keydown.
function normalizeKey(key: string): string {
  const k = key.toLowerCase();
  switch (k) {
    case "arrowup":
      return "up";
    case "arrowdown":
      return "down";
    case "arrowleft":
      return "left";
    case "arrowright":
      return "right";
    case "escape":
      return "esc";
    case " ":
    case "spacebar":
      return "space";
    default:
      return SHIFTED_TO_BASE[k] ?? k;
  }
}

function parseChord(spec: string): Chord {
  const chord: Chord = { mod: false, ctrl: false, shift: false, alt: false, meta: false, key: "" };
  for (const part of spec
    .split("+")
    .map((p) => p.trim())
    .filter((p) => p.length > 0)) {
    switch (part.toLowerCase()) {
      case "$mod":
      case "mod":
        chord.mod = true;
        break;
      case "ctrl":
      case "control":
        chord.ctrl = true;
        break;
      case "shift":
        chord.shift = true;
        break;
      case "alt":
      case "option":
        chord.alt = true;
        break;
      case "meta":
      case "cmd":
      case "command":
      case "win":
      case "super":
        chord.meta = true;
        break;
      default:
        chord.key = normalizeKey(part);
    }
  }
  return chord;
}

function matches(chord: Chord, event: KeyboardEvent): boolean {
  if (chord.key === "") {
    return false; // modifiers-only binding never matches a keydown
  }
  const wantCtrl = chord.ctrl || (chord.mod && !IS_MAC);
  const wantMeta = chord.meta || (chord.mod && IS_MAC);
  return (
    event.ctrlKey === wantCtrl &&
    event.metaKey === wantMeta &&
    event.shiftKey === chord.shift &&
    event.altKey === chord.alt &&
    normalizeKey(event.key) === chord.key
  );
}

let compiled: { chord: Chord; binding: ResolvedKeybinding }[] = [];

function rebuild(): void {
  // Skip global bindings: the host registers them with the OS, so resolving them here too would double-fire
  // them while Weavie is focused.
  compiled = getKeybindings()
    .filter((binding) => binding.global !== true)
    .map((binding) => ({ chord: parseChord(binding.key), binding }));
}

/** Installs the capture-phase keybinding resolver; returns a teardown function. */
export function installKeybindings(): () => void {
  rebuild();
  const offChanged = onCommandsChanged(rebuild);

  const onKeyDown = (event: KeyboardEvent): void => {
    // Last-match-first so a user binding wins over a default for the same key.
    for (let i = compiled.length - 1; i >= 0; i--) {
      const entry = compiled[i];
      if (entry === undefined) {
        continue;
      }
      const { chord, binding } = entry;
      if (!matches(chord, event) || !evaluateWhen(binding.when)) {
        continue;
      }
      if (runForKeybinding(binding.command, binding.args)) {
        event.preventDefault();
        event.stopPropagation();
        return;
      }
      // The command declined (e.g. no pane at that index) — let the keystroke fall through.
    }
  };

  window.addEventListener("keydown", onKeyDown, { capture: true });
  return () => {
    window.removeEventListener("keydown", onKeyDown, { capture: true });
    offChanged();
  };
}

/** Formats a raw key spec for display, e.g. "$mod+Shift+p" → "Ctrl+Shift+P" (or "⌘+Shift+P" on macOS). */
export function formatKey(spec: string): string {
  return spec
    .split("+")
    .map((part) => {
      const lower = part.trim().toLowerCase();
      if (lower === "$mod" || lower === "mod") {
        return IS_MAC ? "⌘" : "Ctrl";
      }
      if (lower === "control") {
        return "Ctrl";
      }
      if (lower.length === 1) {
        return lower.toUpperCase();
      }
      return part.charAt(0).toUpperCase() + part.slice(1);
    })
    .join("+");
}
