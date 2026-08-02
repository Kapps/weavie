import { formatKey } from "./keybindings";
import { findCommand, findCommandInCatalog } from "./registry";

function labels(keys: string[]): string {
  return keys.map(formatKey).join(" / ");
}

/** One backend catalog's effective shortcuts as a bare label, independent of the selected session. */
export function keyLabelInCatalog(backendId: string, commandId: string): string {
  return labels(findCommandInCatalog(backendId, commandId)?.keys ?? []);
}

/**
 * A command's effective shortcut as a label suffix (" (Ctrl+…)"), read live from the catalog so buttons
 * advertise the real (user-overridable) binding; empty when the command is unbound.
 */
export function keyHint(commandId: string): string {
  const keys = labels(findCommand(commandId)?.keys ?? []);
  return keys.length > 0 ? ` (${keys})` : "";
}

/**
 * A command's first effective binding as a bare key label ("Esc"), for inline hint copy; empty when
 * the command is unbound.
 */
export function keyLabel(commandId: string): string {
  const key = findCommand(commandId)?.keys[0];
  return key === undefined ? "" : formatKey(key);
}
