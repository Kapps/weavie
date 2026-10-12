import { createSignal } from "solid-js";

// How the unpinned panel was opened: hovering the rail closes it on leave; the keyboard closes it on Escape.
export type OpenMode = "hover" | "keyboard";
export const [openMode, setOpenMode] = createSignal<OpenMode | null>(null);
const HOVER_OPEN_MS = 150;
const HOVER_CLOSE_MS = 250;
let opening: ReturnType<typeof setTimeout> | undefined;
let closing: ReturnType<typeof setTimeout> | undefined;
// A rail click is a direct choice, so hover stays inert until the pointer is seen outside the rail.
let clicked = false;

// Hover follows where the pointer is seen, not enter/leave events: switching sessions rebuilds the dot under the
// pointer, after which Chrome can report a re-entry with no move and drop the later leave.
export function trackPointer(event: PointerEvent): void {
  locatePointer(event.target instanceof Element ? event.target : null);
}

// Leaving the window or entering an iframe sends no further moves to this document; the pointerout says where it went.
export function trackPointerOut(event: PointerEvent): void {
  const next = event.relatedTarget instanceof Element ? event.relatedTarget : null;
  if (next === null || next instanceof HTMLIFrameElement) locatePointer(null);
}

function locatePointer(target: Element | null): void {
  if (target?.closest(".session-rail, .session-panel") == null) {
    clicked = false;
    clearTimeout(opening);
    opening = undefined;
    if (openMode() === "hover" && closing === undefined) {
      closing = setTimeout(() => {
        closing = undefined;
        if (openMode() === "hover") setOpenMode(null);
      }, HOVER_CLOSE_MS);
    }
    return;
  }
  clearTimeout(closing);
  closing = undefined;
  if (target.closest(".session-rail") === null || clicked) return;
  if (openMode() === null && opening === undefined) {
    opening = setTimeout(() => {
      opening = undefined;
      if (openMode() === null) setOpenMode("hover");
    }, HOVER_OPEN_MS);
  }
}

/** A click on the rail cancels a pending or hover-opened panel until the pointer leaves the rail. */
export function cancelSessionPanelHover(): void {
  clicked = true;
  clearTimeout(opening);
  opening = undefined;
  if (openMode() === "hover") setOpenMode(null);
}
