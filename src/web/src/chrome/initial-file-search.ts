import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { selectedSession, selectionRestored } from "../bridge";
import { editorSessionFor } from "../editor/session-store";
import { gettingStartedOpen } from "../getting-started/state";
import { onSplashDismissed } from "../splash";
import { themePickerOpen } from "../theme/picker-state";
import { modalActive } from "./modal-state";
import { focusOmnibar } from "./omnibar-controller";
import { sessionInfo } from "./session-store";

/** Opens file search once for an unused, empty desktop session, after startup and setup finish. */
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
    const owner = selectedSession();
    if (!selectionRestored() || owner === null || owner.state.editor.current === null) return;
    if (sessionInfo(owner) === undefined || editorSessionFor(owner) === null) return;
    // Let a closing setup dialog release its modal slot before focusing the workspace.
    queueMicrotask(() => {
      if (!pending || gettingStartedOpen() || themePickerOpen()) return;
      if (!selectionRestored() || selectedSession() !== owner) return;
      stop();
      if (
        !compact() &&
        sessionInfo(owner)?.lastActiveAt === null &&
        editorSessionFor(owner)?.open.length === 0 &&
        !modalActive()
      ) {
        focusOmnibar("file");
      }
    });
  });
}
