import { createEffect, createRoot, createSignal, on } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID, registerHostFeature } from "../bridge";
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
registerHostFeature((connection) =>
  connection.isLocal
    ? connection.host.feature("gettingStarted").on("show", () => {
        openGettingStarted(0);
      })
    : undefined,
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

/** One of the local host's features, for request/response calls. */
export function localFeature(name: string) {
  const connection = hostConnection(LOCAL_BACKEND_ID);
  if (connection === undefined) throw new Error("The Weavie host is not connected.");
  return connection.host.feature(name);
}

const settings = () => localFeature("settings");

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
