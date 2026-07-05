import { formatKey } from "./keybindings";
import { findCommand } from "./registry";

/**
 * A command's effective shortcut as a label suffix (" (Ctrl+…)"), read live from the catalog so buttons
 * advertise the real (user-overridable) binding; empty when the command is unbound.
 */
export function keyHint(commandId: string): string {
  const keys = findCommand(commandId)?.keys ?? [];
  return keys.length > 0 ? ` (${keys.map(formatKey).join(" / ")})` : "";
}
