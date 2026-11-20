import { createEffect, createRoot, createSignal, on } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID, registerHomeFeature } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { openThemeRegistry, themePickerOpen } from "../theme/picker-state";

export const COMPLETED_SETTING = "gettingStarted.completed";

/** One opening of Getting Started: the step it opens on, and the backend whose machine its suggestions belong to. */
export interface SetupRequest {
  step: number;
  machine: string;
}

/** The Getting Started modal showing over the workspace, or null; each opening is a new request. */
export const [gettingStarted, setGettingStarted] = createSignal<SetupRequest | null>(null);

/** Whether the Getting Started modal is showing over the workspace. */
export const gettingStartedOpen = (): boolean => gettingStarted() !== null;

/** Opens Getting Started on `step`, editing suggestions on `machine`'s backend. */
export function openGettingStarted(step: number, machine: string): void {
  setGettingStarted({ step, machine });
}

registerCommand(CommandIds.gettingStarted, () => openGettingStarted(0, LOCAL_BACKEND_ID));

// The host asks for setup on connect while it hasn't been finished or dismissed.
registerHomeFeature((connection) =>
  connection.host.feature("gettingStarted").on("show", () => {
    openGettingStarted(0, LOCAL_BACKEND_ID);
  }),
);

// Only one modal shows at a time, so the setup modal steps aside for the theme picker and returns after it.
let resumeAfterThemes: SetupRequest | null = null;

/** Opens the theme picker on the Open VSX catalog, returning to setup when the picker closes. */
export function browseThemes(): void {
  resumeAfterThemes = gettingStarted();
  setGettingStarted(null);
  openThemeRegistry();
}

createRoot(() =>
  createEffect(
    on(
      themePickerOpen,
      (open) => {
        if (!open && resumeAfterThemes !== null) {
          openGettingStarted(0, resumeAfterThemes.machine);
          resumeAfterThemes = null;
        }
      },
      { defer: true },
    ),
  ),
);

/** One of `backendId`'s host features; the home is `LOCAL_BACKEND_ID`. */
export function setupFeature(backendId: string, name: string) {
  const connection = hostConnection(backendId);
  if (connection === undefined) throw new Error("That machine isn't connected.");
  return connection.host.feature(name);
}

/** Reads one setting's effective value on `backendId`'s host, and whether it's still the default. */
export async function readSetting<T>(
  backendId: string,
  key: string,
): Promise<{ value: T; isDefault: boolean }> {
  const setting = await setupFeature(backendId, "settings").request<
    { value: T; source: string },
    { key: string }
  >("get", { key });
  return { value: setting.value, isDefault: setting.source === "default" };
}

/** Writes one setting on `backendId`'s host; rejects when it's invalid or an env var overrides it. */
export function writeSetting(backendId: string, key: string, value: unknown): Promise<void> {
  return setupFeature(backendId, "settings").request<void, { key: string; value: unknown }>("set", {
    key,
    value,
  });
}
