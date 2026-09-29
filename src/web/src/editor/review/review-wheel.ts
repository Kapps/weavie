import type { ReviewScroll } from "./review-scroll";

/** Horizontal input belongs to the file; a diagonal gesture also advances the outer review. */
export function routeReviewWheel(
  event: WheelEvent,
  scroll: ReviewScroll,
  lineHeight: number,
  width: number,
  horizontal: (delta: number) => void,
): void {
  event.stopPropagation();
  const delta = event.deltaX || (event.shiftKey ? event.deltaY : 0);
  if (delta === 0) {
    scroll.wheel(event);
    return;
  }
  const unit =
    event.deltaMode === WheelEvent.DOM_DELTA_LINE
      ? lineHeight
      : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
        ? width
        : 1;
  horizontal(delta * unit);
  if (event.shiftKey || event.deltaY === 0) event.preventDefault();
  else scroll.wheel(event);
}
