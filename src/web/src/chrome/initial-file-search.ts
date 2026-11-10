import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { editorSession } from "../editor/session-store";
import { gettingStartedOpen } from "../getting-started/state";
import { onSplashDismissed } from "../splash";
import { themePickerOpen } from "../theme/picker-state";
import { modalActive } from "./modal-state";
import { focusOmnibar } from "./omnibar-controller";

/** Opens file search once for an empty desktop workspace, after startup and setup finish. */
export function createInitialFileSearch(compact: () => boolean): void {
  const [revealed, setRevealed] = createSignal(false);
  let pending = true;
  const interacted = () => {
    if (!modalActive()) stop();
  };
  const stop = () => {
    pending = false;
    window.removeEventListener("pointerdown", interacted, true);
    window.removeEventListener("keydown", interacted, true);
  };
  window.addEventListener("pointerdown", interacted, true);
  window.addEventListener("keydown", interacted, true);
  onCleanup(stop);
  onMount(() => onCleanup(onSplashDismissed(() => setRevealed(true))));

  createEffect(() => {
    if (!pending || !revealed() || gettingStartedOpen() || themePickerOpen()) return;
    if (editorSession() === null) return;
    // Let a closing setup dialog release its modal slot before focusing the workspace.
    queueMicrotask(() => {
      if (!pending || gettingStartedOpen() || themePickerOpen()) return;
      stop();
      if (!compact() && editorSession()?.open.length === 0 && !modalActive()) {
        focusOmnibar("file");
      }
    });
  });
}
