import { createEffect, createRoot, createSignal, on } from "solid-js";
import { homeFeature, registerHomeFeature } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { openThemeRegistry, themePickerOpen } from "../theme/picker-state";

export const COMPLETED_SETTING = "gettingStarted.completed";

/** Whether the Getting Started modal is showing over the workspace. */
export const [gettingStartedOpen, setGettingStartedOpen] = createSignal(false);

/** The step Getting Started opens on. */
export const [gettingStartedStep, setGettingStartedStep] = createSignal(0);

/** Opens Getting Started on `step`. */
export function openGettingStarted(step: number): void {
  setGettingStartedStep(step);
  setGettingStartedOpen(true);
}

registerCommand(CommandIds.gettingStarted, () => openGettingStarted(0));

// The host asks for setup on connect while it hasn't been finished or dismissed.
registerHomeFeature((connection) =>
  connection.host.feature("gettingStarted").on("show", () => {
    openGettingStarted(0);
  }),
);

// Only one modal shows at a time, so the setup modal steps aside for the theme picker and returns after it.
let resumeAfterThemes = false;

/** Opens the theme picker on the Open VSX catalog, returning to setup when the picker closes. */
export function browseThemes(): void {
  resumeAfterThemes = gettingStartedOpen();
  setGettingStartedOpen(false);
  openThemeRegistry();
}

createRoot(() =>
  createEffect(
    on(
      themePickerOpen,
      (open) => {
        if (!open && resumeAfterThemes) {
          resumeAfterThemes = false;
          openGettingStarted(0);
        }
      },
      { defer: true },
    ),
  ),
);

const settings = () => homeFeature("settings");

/** Reads one global setting's effective value from the local host, and whether it's still the default. */
export async function readSetting<T>(key: string): Promise<{ value: T; isDefault: boolean }> {
  const setting = await settings().request<{ value: T; source: string }, { key: string }>("get", {
    key,
  });
  return { value: setting.value, isDefault: setting.source === "default" };
}

/** Writes one global setting on the local host; rejects when it's invalid or an env var overrides it. */
export function writeSetting(key: string, value: unknown): Promise<void> {
  return settings().request<void, { key: string; value: unknown }>("set", { key, value });
}
