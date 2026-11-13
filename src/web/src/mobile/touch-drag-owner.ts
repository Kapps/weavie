/** Controls that take a touch themselves, so no pane gesture starts on them. */
export const TOUCH_CONTROLS =
  "button, a, input, textarea, select, summary, [contenteditable], [role='button'], [role='link'], [role='menuitem'], [role='option']";

/** How far a vertical scroller can still move toward its start; a bottom-origin one counts down from zero. */
export function roomAbove(
  scroller: { clientHeight: number; scrollHeight: number; scrollTop: number },
  bottomOrigin: boolean,
): number {
  return bottomOrigin
    ? scroller.scrollHeight - scroller.clientHeight + scroller.scrollTop
    : scroller.scrollTop;
}
