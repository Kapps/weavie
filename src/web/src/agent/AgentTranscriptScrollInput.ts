import {
  type IMouseWheelEvent,
  StandardWheelEvent,
} from "@codingame/monaco-vscode-api/vscode/vs/base/browser/mouseEvent";
import { MouseWheelClassifier } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/scrollbar/scrollableElement";

export function wheelMotion(
  event: WheelEvent,
  viewport: HTMLElement,
  smoothScrolling: boolean,
): { delta: number; animate: boolean } | null {
  if (
    event.defaultPrevented ||
    !event.cancelable ||
    event.ctrlKey ||
    event.metaKey ||
    event.shiftKey ||
    event.deltaY === 0 ||
    Math.abs(event.deltaX) > Math.abs(event.deltaY)
  )
    return null;
  for (const target of event.composedPath()) {
    if (target === viewport) break;
    if (!(target instanceof HTMLElement)) continue;
    const style = getComputedStyle(target);
    if (!/^(auto|scroll)$/.test(style.overflowY)) continue;
    const canScroll =
      event.deltaY < 0
        ? target.scrollTop > 0
        : target.scrollTop + target.clientHeight < target.scrollHeight;
    if (canScroll || style.overscrollBehaviorY !== "auto") return null;
  }
  const unit =
    event.deltaMode === WheelEvent.DOM_DELTA_LINE
      ? Number.parseFloat(getComputedStyle(viewport).lineHeight)
      : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
        ? viewport.clientHeight
        : 1;
  const classifier = MouseWheelClassifier.INSTANCE;
  classifier.acceptStandardWheelEvent(
    new StandardWheelEvent(event as WheelEvent & IMouseWheelEvent),
  );
  const precise =
    event.deltaMode === WheelEvent.DOM_DELTA_PIXEL &&
    (Math.abs(event.deltaY) <= 1 || !Number.isInteger(event.deltaY));
  return {
    delta: event.deltaY * unit,
    animate: smoothScrolling && !precise && classifier.isPhysicalMouseWheel(),
  };
}

export function ownsTouchGesture(
  initialTarget: EventTarget,
  deltaX: number,
  deltaY: number,
  viewport: HTMLElement,
): boolean {
  if (deltaY === 0 || Math.abs(deltaX) >= Math.abs(deltaY)) return false;
  const selection = viewport.ownerDocument.getSelection();
  if (selection !== null && !selection.isCollapsed && viewport.contains(selection.anchorNode))
    return false;
  let target = initialTarget instanceof Element ? initialTarget : null;
  while (target !== null && target !== viewport) {
    if (target instanceof HTMLElement) {
      if (target.matches("input, textarea, select") || target.isContentEditable) return false;
      const style = getComputedStyle(target);
      if (
        /^(auto|scroll)$/.test(style.overflowY) &&
        (target.scrollHeight > target.clientHeight || style.overscrollBehaviorY !== "auto")
      )
        return false;
    }
    target = target.parentElement;
  }
  return target === viewport;
}
