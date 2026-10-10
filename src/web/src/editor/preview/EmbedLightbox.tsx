import { createEffect, createMemo, createSignal, type JSX, onCleanup, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { selectedSession } from "../../bridge";
import { modalActive, requestModal } from "../../chrome/modal-state";
import { createPanZoom } from "../pan-zoom";
import type { EmbedZoomState } from "./embed-zoom";

/**
 * Full-app lightbox for a preview embed: a Portal to body so it paints over every pane (terminal
 * included), showing a clone of the zoomed image / Mermaid diagram at viewport size. A capture-phase
 * listener owns Escape (close), the arrow keys (step) and +/-/0 (zoom) so they never reach the focused
 * editor/terminal. The app's modal slot blocks unrelated keybindings while the lightbox is open.
 * Zoom/pan gestures come from createPanZoom; stepping to another embed resets to the fitted view.
 */
export function EmbedLightbox(props: {
  state: () => EmbedZoomState | null;
  onStep: (delta: number) => void;
  onClose: () => void;
}): JSX.Element {
  let frame!: HTMLDivElement;
  let dialog: HTMLDivElement | undefined;
  const [active, setActive] = createSignal(false);
  const zoom = createPanZoom(() => frame);

  // Stepping to another embed (a new state object) starts it at the fitted view.
  createEffect(() => {
    props.state();
    zoom.reset();
  });

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Tab") {
      event.preventDefault();
      event.stopImmediatePropagation();
      dialog?.focus();
      return;
    }
    if (event.ctrlKey || event.metaKey || event.altKey) {
      return; // modifier chords (e.g. the global font zoom) aren't the lightbox's to consume
    }
    const actions: Record<string, () => void> = {
      Escape: () => props.onClose(),
      ArrowRight: () => props.onStep(1),
      ArrowLeft: () => props.onStep(-1),
    };
    const action = actions[event.key];
    if (action !== undefined) {
      action();
    } else if (!zoom.zoomKey(event.key)) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
  };
  const owner = createMemo(() => props.state()?.session ?? null);
  createEffect(() => {
    const session = owner();
    if (session === null) {
      return;
    }
    let restoreFocus: HTMLElement | null = null;
    let restoreZoomIndex: number | null = null;
    let activated = false;
    const releaseModal = requestModal(() => {
      activated = true;
      restoreFocus =
        document.activeElement instanceof HTMLElement && document.activeElement !== document.body
          ? document.activeElement
          : null;
      const state = props.state();
      restoreZoomIndex =
        restoreFocus?.matches(".embed-zoom-btn") === true && state !== null ? state.index : null;
      setActive(true);
      window.addEventListener("keydown", onKeyDown, { capture: true });
    });
    onCleanup(() => {
      if (activated) {
        window.removeEventListener("keydown", onKeyDown, { capture: true });
      }
      const focusStayedInModal =
        document.activeElement === document.body ||
        dialog?.contains(document.activeElement) === true;
      setActive(false);
      releaseModal();
      const target = restoreFocus?.isConnected
        ? restoreFocus
        : restoreZoomIndex === null
          ? null
          : currentZoomButton(restoreZoomIndex);
      if (
        !modalActive() &&
        selectedSession() === session &&
        focusStayedInModal &&
        target?.isConnected
      ) {
        target.focus();
      }
    });
  });

  const hint = (state: EmbedZoomState): string => {
    const parts: string[] = [];
    if (state.targets.length > 1) {
      parts.push(`${state.index + 1} / ${state.targets.length} (←/→)`);
    }
    if (zoom.hint().length > 0) {
      parts.push(zoom.hint());
    }
    return parts.join(" · ");
  };

  return (
    <Portal>
      <Show when={active() ? props.state() : null}>
        {(state) => (
          <div
            class="embed-lightbox"
            role="dialog"
            aria-modal="true"
            aria-label="Zoomed embed"
            data-kind="editor"
            data-surface="editor"
            tabindex="0"
            onPointerDown={() => props.onClose()}
            ref={(el) => {
              dialog = el;
              el.addEventListener("wheel", zoom.onWheel, { passive: false });
              queueMicrotask(() => {
                if (dialog === el && el.isConnected) {
                  el.focus();
                }
              });
            }}
          >
            <div
              class="embed-lightbox-body"
              role="application"
              classList={{ zoomed: zoom.scale() > 1, panning: zoom.panning() }}
              ref={frame}
              {...zoom.handlers}
              onPointerDown={(event) => {
                // Clicks inside never dismiss via the backdrop.
                event.stopPropagation();
                zoom.handlers.onPointerDown(event);
              }}
            >
              {/* String-valued style: the style-object form compiles to setStyleProperty, which this
                solid-js runtime doesn't export (version skew with the JSX transform). */}
              <div class="embed-lightbox-zoom" style={`transform: ${zoom.transform()}`}>
                {/* The index is always in range: opening sets a hit and stepping wraps. */}
                {zoomClone(state().targets[state().index]!)}
              </div>
            </div>
            <Show when={hint(state()).length > 0}>
              <div class="embed-lightbox-count">{hint(state())}</div>
            </Show>
          </div>
        )}
      </Show>
    </Portal>
  );
}

// A display clone of the embed, stripped of the in-preview dressing: the magnifier button (a Mermaid
// wrapper carries it as a child) and Mermaid's inline size caps, so the vector can fill the lightbox.
function zoomClone(target: HTMLElement): HTMLElement {
  const clone = target.cloneNode(true) as HTMLElement;
  clone.classList.remove("embed-zoom");
  clone.querySelector(".embed-zoom-btn")?.remove();
  const svg = clone.querySelector("svg");
  if (svg !== null) {
    svg.style.maxWidth = "";
    svg.removeAttribute("width");
    svg.removeAttribute("height");
  }
  return clone;
}

function currentZoomButton(index: number): HTMLElement | null {
  const root: ParentNode | null =
    document.querySelector(".editor-preview-body") ??
    document.querySelector<HTMLElement>(".editor-source")?.shadowRoot ??
    null;
  return root?.querySelectorAll<HTMLElement>(".embed-zoom-btn").item(index) ?? null;
}
