// Presses on empty title-bar space (elements marked `data-caption`), sent to the local host, which decides whether
// they move, zoom, or open the window menu. Installed once per page, so no bar wires its own handlers.

import { localWindowFeature, windowChrome } from "./window-state";

function isCaption(e: MouseEvent): boolean {
  return (
    windowChrome()?.caption === "press" &&
    e.target instanceof Element &&
    e.target.hasAttribute("data-caption")
  );
}

/** Installs caption-press reporting for the page; returns a teardown. */
export function installCaptionPress(): () => void {
  const press = (e: MouseEvent): void => {
    if (!isCaption(e)) return;
    // Keeps focus and selection where they were, as a native title bar does.
    e.preventDefault();
    localWindowFeature()?.publish("captionPress", { button: e.button, count: e.detail });
  };
  // A right-click on empty bar space belongs to the host's window menu.
  const menu = (e: MouseEvent): void => {
    if (isCaption(e)) e.preventDefault();
  };
  document.addEventListener("mousedown", press, true);
  document.addEventListener("contextmenu", menu, true);
  return () => {
    document.removeEventListener("mousedown", press, true);
    document.removeEventListener("contextmenu", menu, true);
  };
}
