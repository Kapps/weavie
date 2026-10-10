import { createEffect, createRoot, createSignal, on } from "solid-js";
import { registerHostFeature } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { openThemeRegistry, themePickerOpen } from "../theme/picker-state";

export const COMPLETED_SETTING = "gettingStarted.completed";

/** Whether the Getting Started modal is showing over the workspace. */
export const [gettingStartedOpen, setGettingStartedOpen] = createSignal(false);

registerCommand(CommandIds.gettingStarted, () => {
  setGettingStartedOpen(true);
});

// The host asks for setup on connect while it hasn't been finished or dismissed.
registerHostFeature((connection) =>
  connection.isLocal
    ? connection.host.feature("gettingStarted").on("show", () => {
        setGettingStartedOpen(true);
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
          setGettingStartedOpen(true);
        }
      },
      { defer: true },
    ),
  ),
);
