import { currentEditorOptions } from "../editor-options";

const DEAD_ZONE = 5;
const EXCLUDED = "a,button,input,textarea,select,[contenteditable]:not([contenteditable='false'])";

/** Adds middle-click autoscroll to one Linux structured-agent transcript. */
export function installAgentMiddleClickAutoscroll(element: HTMLElement): () => void {
  if (window.__WEAVIE_SHELL__?.platform !== "linux") {
    return () => {};
  }

  let scrolling = false;
  let frame = 0;
  let lastFrame = 0;
  let originY = 0;
  let pointerY = 0;
  let movedWhileHeld = false;
  let held: AbortController | null = null;
  const stop = (): void => {
    scrolling = false;
    cancelAnimationFrame(frame);
    frame = 0;
    held?.abort();
    held = null;
    element.classList.remove("agent-middle-click-autoscrolling");
  };
  const animate = (time: number): void => {
    if (!scrolling || !currentEditorOptions().middleClickAutoscroll) {
      stop();
      return;
    }
    if (lastFrame !== 0) {
      const distance = pointerY - originY;
      const velocity = Math.sign(distance) * Math.max(Math.abs(distance) - DEAD_ZONE, 0);
      element.scrollTop += (velocity * (time - lastFrame)) / 32;
    }
    lastFrame = time;
    frame = requestAnimationFrame(animate);
  };
  const consume = (event: MouseEvent): void => {
    event.preventDefault();
    event.stopPropagation();
  };
  const onMouseDown = (event: MouseEvent): void => {
    if (scrolling) {
      stop();
      return;
    }
    const target = event.target;
    if (
      event.button !== 1 ||
      event.altKey ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey ||
      !currentEditorOptions().middleClickAutoscroll ||
      !(target instanceof Element) ||
      !element.contains(target) ||
      target.closest(EXCLUDED) !== null ||
      element.scrollHeight <= element.clientHeight
    ) {
      return;
    }
    consume(event);
    // Bound to the drag: a window-level wheel listener — passive or not — takes the page off WebKit's
    // async-scrolling path, so it must not outlive an active autoscroll.
    held = new AbortController();
    const heldOptions = { capture: true, signal: held.signal };
    window.addEventListener("mousemove", onMouseMove, heldOptions);
    window.addEventListener("mouseup", onMouseUp, heldOptions);
    window.addEventListener("keydown", onKeyDown, heldOptions);
    window.addEventListener("wheel", stop, heldOptions);
    window.addEventListener("blur", stop, { signal: held.signal });
    scrolling = true;
    originY = pointerY = event.clientY;
    movedWhileHeld = false;
    lastFrame = 0;
    element.style.setProperty("--agent-autoscroll-x", `${event.clientX}px`);
    element.style.setProperty("--agent-autoscroll-y", `${event.clientY}px`);
    element.classList.add("agent-middle-click-autoscrolling");
    frame = requestAnimationFrame(animate);
  };
  const onMouseMove = (event: MouseEvent): void => {
    pointerY = event.clientY;
    movedWhileHeld ||= scrolling && Math.abs(pointerY - originY) > DEAD_ZONE;
  };
  const onMouseUp = (event: MouseEvent): void => {
    if (
      event.button === 1 &&
      scrolling &&
      (movedWhileHeld || Math.abs(event.clientY - originY) > DEAD_ZONE)
    ) {
      consume(event);
      stop();
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      stop();
    }
  };
  const controller = new AbortController();
  document.addEventListener("mousedown", onMouseDown, {
    capture: true,
    signal: controller.signal,
  });
  return () => {
    controller.abort();
    stop();
  };
}
