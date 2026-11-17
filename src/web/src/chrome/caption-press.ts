// The page side of the window's title bar: presses on empty bar space and window-button clicks, sent to the
// local host. Empty bar space is any element marked `data-caption`, so a control inside the bar never drags.

import type { JSX } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID } from "../bridge";
import { windowChrome } from "./window-state";

const windowFeature = () => hostConnection(LOCAL_BACKEND_ID)?.host.feature("window");

function isCaption(e: MouseEvent): boolean {
  return (
    windowChrome()?.caption === "press" &&
    e.target instanceof Element &&
    e.target.hasAttribute("data-caption")
  );
}

/** Reports a press on empty bar space; the host decides whether it moves, zooms, or opens the window menu. */
export const onCaptionMouseDown: JSX.EventHandler<HTMLElement, MouseEvent> = (e) => {
  if (!isCaption(e)) return;
  // Keeps focus and selection where they were, as a native title bar does; a middle press must not autoscroll.
  e.preventDefault();
  e.stopPropagation();
  windowFeature()?.publish("captionPress", { button: e.button, count: e.detail });
};

/** Leaves a right-click on empty bar space to the host's window menu. */
export const onCaptionContextMenu: JSX.EventHandler<HTMLElement, MouseEvent> = (e) => {
  if (isCaption(e)) e.preventDefault();
};

/** Sends a window-button click to the host. */
export function publishWindowControl(action: WindowControlAction): void {
  windowFeature()?.publish("control", { action });
}
