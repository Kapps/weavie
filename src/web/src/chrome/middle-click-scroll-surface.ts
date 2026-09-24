export interface MiddleClickScrollAxis {
  element: Element;
  scrollBy(delta: number): void;
  live: boolean;
}

export interface MiddleClickScrollSurface {
  x: MiddleClickScrollAxis | null;
  y: MiddleClickScrollAxis | null;
}

const registrations = new WeakMap<
  Element,
  { acceptsTarget: (target: Element) => boolean; surface: MiddleClickScrollSurface }
>();
const OWNS_MIDDLE_CLICK =
  "a,input,textarea,select,[contenteditable]:not([contenteditable='false']),.xterm,[data-middle-click]";
const SCROLLABLE = /^(auto|scroll|overlay)$/;

/** Registers virtual scroll axes with their owning surface, independently of disposable descendants. */
export function registerMiddleClickScroll(
  element: Element,
  acceptsTarget: (target: Element) => boolean,
  scroll: { x: ((delta: number) => void) | null; y: ((delta: number) => void) | null },
): () => void {
  const axis = (scrollBy: ((delta: number) => void) | null): MiddleClickScrollAxis | null =>
    scrollBy === null ? null : { element, scrollBy, live: true };
  const surface = { x: axis(scroll.x), y: axis(scroll.y) };
  registrations.set(element, { acceptsTarget, surface });
  return () => {
    registrations.delete(element);
    if (surface.x) surface.x.live = false;
    if (surface.y) surface.y.live = false;
  };
}

/** Resolves the nearest owner per axis; unregistered editors and nested widgets keep their gestures. */
export function middleClickSurfaceAt(target: Element): MiddleClickScrollSurface | null {
  if (target.closest(OWNS_MIDDLE_CLICK)) return null;
  const editor = target.closest(".monaco-editor");
  if (editor && !registrations.get(editor)?.acceptsTarget(target)) return null;
  const surface: MiddleClickScrollSurface = { x: null, y: null };
  for (let node: Element | null = target; node !== null; node = node.parentElement) {
    const registered = registrations.get(node);
    if (registered?.acceptsTarget(target)) {
      surface.x ??= registered.surface.x;
      surface.y ??= registered.surface.y;
    } else {
      const element = node;
      const style = getComputedStyle(element);
      if (
        !surface.x &&
        SCROLLABLE.test(style.overflowX) &&
        element.scrollWidth - element.clientWidth > 1
      ) {
        surface.x = {
          element,
          live: true,
          scrollBy: (delta) => {
            element.scrollLeft += delta;
          },
        };
      }
      if (
        !surface.y &&
        SCROLLABLE.test(style.overflowY) &&
        element.scrollHeight - element.clientHeight > 1
      ) {
        surface.y = {
          element,
          live: true,
          scrollBy: (delta) => {
            element.scrollTop += delta;
          },
        };
      }
    }
    if (surface.x && surface.y) break;
  }
  return surface.x === null && surface.y === null ? null : surface;
}
