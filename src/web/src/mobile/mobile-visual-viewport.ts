import { type Accessor, createEffect, createSignal, onCleanup } from "solid-js";

/** Tracks the visible compact-app bounds as mobile browser chrome and the software keyboard move. */
export function createMobileVisualViewportStyle(compact: Accessor<boolean>): Accessor<string> {
  const viewport = window.visualViewport;
  const standalone =
    window.matchMedia("(display-mode: standalone)").matches ||
    (navigator as Navigator & { standalone?: boolean }).standalone === true;
  const [style, setStyle] = createSignal("");
  let layoutHeight = window.innerHeight;
  let unobscuredHeight = viewport?.height ?? layoutHeight;

  createEffect(() => {
    if (!compact() || viewport === null) {
      setStyle("");
      return;
    }

    const update = (): void => {
      if (layoutHeight !== window.innerHeight) {
        layoutHeight = window.innerHeight;
        unobscuredHeight = viewport.height;
      }
      const useLayoutViewport = standalone && viewport.height >= unobscuredHeight;
      const height = useLayoutViewport ? layoutHeight : viewport.height;
      const top = useLayoutViewport ? 0 : viewport.offsetTop;
      setStyle(`--mobile-viewport-height:${height}px;--mobile-viewport-top:${top}px;`);
      resetDocumentScroll();
    };
    const resetDocumentScroll = (): void => window.scrollTo(0, 0);
    update();
    window.addEventListener("resize", update);
    window.addEventListener("scroll", resetDocumentScroll);
    viewport.addEventListener("resize", update);
    viewport.addEventListener("scroll", update);
    onCleanup(() => {
      window.removeEventListener("resize", update);
      window.removeEventListener("scroll", resetDocumentScroll);
      viewport.removeEventListener("resize", update);
      viewport.removeEventListener("scroll", update);
    });
  });

  return style;
}
