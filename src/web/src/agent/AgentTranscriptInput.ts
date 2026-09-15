import { registerScrollTarget } from "../chrome/middle-click-autoscroll";
import { currentEditorOptions } from "../editor-options";

export interface TranscriptInputTarget {
  height: () => number;
  contentHeight: () => number;
  scrollBy: (delta: number, animate: boolean) => void;
  jumpTo: (offset: number) => void;
}

export function installTranscriptInput(
  body: HTMLElement,
  target: TranscriptInputTarget,
  keyboardElement: HTMLElement,
): () => void {
  const unregister = registerScrollTarget(body, {
    x: null,
    y: {
      canScroll: () => target.contentHeight() > target.height(),
      scrollBy: (delta) => target.scrollBy(delta, false),
    },
  });
  const keydown = (event: KeyboardEvent): void => {
    if (
      (event.target !== body && event.target !== keyboardElement) ||
      event.defaultPrevented ||
      event.isComposing ||
      event.altKey ||
      event.ctrlKey ||
      event.metaKey ||
      (event.shiftKey && event.key !== " ")
    )
      return;
    let delta: number;
    switch (event.key) {
      case "ArrowUp":
        delta = -Number.parseFloat(getComputedStyle(body).lineHeight);
        break;
      case "ArrowDown":
        delta = Number.parseFloat(getComputedStyle(body).lineHeight);
        break;
      case "PageUp":
        delta = -target.height();
        break;
      case "PageDown":
        delta = target.height();
        break;
      case " ":
        delta = target.height() * (event.shiftKey ? -1 : 1);
        break;
      case "Home":
        event.preventDefault();
        target.jumpTo(0);
        return;
      case "End":
        event.preventDefault();
        target.jumpTo(target.contentHeight());
        return;
      default:
        return;
    }
    event.preventDefault();
    target.scrollBy(delta, currentEditorOptions().smoothScrolling);
  };
  body.addEventListener("keydown", keydown);
  return () => {
    body.removeEventListener("keydown", keydown);
    unregister();
  };
}
