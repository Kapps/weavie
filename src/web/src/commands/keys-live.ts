import { createSignal } from "solid-js";
import { keyLabel } from "./key-hint";
import { onCommandsChanged } from "./registry";

// App-lifetime subscription: visible key labels re-resolve when the catalog/keybindings change.
const [version, setVersion] = createSignal(0);
onCommandsChanged(() => setVersion((current) => current + 1));

/** `keyLabel` that tracks catalog changes — use for key text rendered on screen, not tooltips. */
export function liveKeyLabel(commandId: string): string {
  version();
  return keyLabel(commandId);
}
