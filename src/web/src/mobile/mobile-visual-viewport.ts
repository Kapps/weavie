import { type Accessor, createEffect, createSignal, onCleanup } from "solid-js";

/** Tracks the visible compact-app bounds as mobile browser chrome and the software keyboard move. */
export function createMobileVisualViewportStyle(compact: Accessor<boolean>): Accessor<string> {
  const viewport = window.visualViewport;
  const [style, setStyle] = createSignal("");

  createEffect(() => {
    if (!compact() || viewport === null) {
      setStyle("");
      return;
    }

    const update = (): void => {
      setStyle(
        `--mobile-viewport-height:${viewport.height}px;--mobile-viewport-top:${viewport.offsetTop}px;`,
      );
    };
    update();
    viewport.addEventListener("resize", update);
    viewport.addEventListener("scroll", update);
    onCleanup(() => {
      viewport.removeEventListener("resize", update);
      viewport.removeEventListener("scroll", update);
    });
  });

  return style;
}
