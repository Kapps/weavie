import { agentMiddleClickAutoscrollEnabled } from "../chrome/agent-default";

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
  const stop = (): void => {
    scrolling = false;
    cancelAnimationFrame(frame);
    frame = 0;
    element.classList.remove("agent-middle-click-autoscrolling");
  };
  const animate = (time: number): void => {
    if (!scrolling || !agentMiddleClickAutoscrollEnabled()) {
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
      !agentMiddleClickAutoscrollEnabled() ||
      !(target instanceof Element) ||
      !element.contains(target) ||
      target.closest(EXCLUDED) !== null ||
      element.scrollHeight <= element.clientHeight
    ) {
      return;
    }
    consume(event);
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
  const options = { capture: true, signal: controller.signal };
  document.addEventListener("mousedown", onMouseDown, options);
  window.addEventListener("mousemove", onMouseMove, options);
  window.addEventListener("mouseup", onMouseUp, options);
  window.addEventListener("keydown", onKeyDown, options);
  window.addEventListener("wheel", stop, options);
  window.addEventListener("blur", stop, { signal: controller.signal });
  return () => {
    controller.abort();
    stop();
  };
}
