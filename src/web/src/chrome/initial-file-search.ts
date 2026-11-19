import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { type ClientSession, isSelected, selectionRestored } from "../bridge";
import { editorSessionFor } from "../editor/session-store";
import { gettingStartedOpen } from "../getting-started/state";
import { onSplashDismissed } from "../splash";
import { themePickerOpen } from "../theme/picker-state";
import { modalActive } from "./modal-state";
import { focusOmnibar } from "./omnibar-controller";
import { sessionInfo } from "./session-store";

/** Opens file search once when `session`, the one on screen, is unused and empty, after startup and setup finish. */
export function createInitialFileSearch(
  compact: () => boolean,
  session: () => ClientSession | null,
): void {
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
    const owner = session();
    if (!selectionRestored() || owner === null || owner.state.editor.current === null) return;
    if (sessionInfo(owner) === undefined || editorSessionFor(owner) === null) return;
    // Let a closing setup dialog release its modal slot before focusing the workspace.
    queueMicrotask(() => {
      if (!pending || gettingStartedOpen() || themePickerOpen()) return;
      if (!selectionRestored() || !isSelected(owner)) return;
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
