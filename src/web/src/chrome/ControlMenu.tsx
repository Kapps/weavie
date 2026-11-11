import { createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { AgentControlAxis, AgentControlOption } from "../bridge";
import { createListNavigation } from "../list-navigation";
import { dismissOnOutsideInteraction } from "./popover-dismiss";
import "./control-menu.css";

const [openMenus, setOpenMenus] = createSignal(0);

/** Whether a control menu is open, so an enclosing dialog leaves Enter and Escape to it. */
export const controlMenuOpen = (): boolean => openMenus() > 0;

/** An option the menu may list without letting it be picked; its description says why. */
export interface ControlMenuOption extends AgentControlOption {
  unavailable?: boolean;
}

const MENU_MIN_WIDTH = 200;
const VIEWPORT_MARGIN = 8;

/**
 * Fixed below the anchor, or above it when the viewport has no room underneath; from the anchor's left edge, or
 * its right edge when the menu's least width wouldn't fit before the viewport's.
 */
export function controlMenuPlacement(anchor: DOMRect): JSX.CSSProperties {
  const below = window.innerHeight - anchor.bottom;
  const above = below < 180 && anchor.top > below;
  const width = Math.max(anchor.width, MENU_MIN_WIDTH);
  const fromRight = anchor.left + width > window.innerWidth - VIEWPORT_MARGIN;
  return {
    position: "fixed",
    ...(fromRight
      ? {
          right: `${window.innerWidth - anchor.right}px`,
          "max-width": `${anchor.right - VIEWPORT_MARGIN}px`,
        }
      : { left: `${anchor.left}px` }),
    "min-width": `${Math.min(width, anchor.right - VIEWPORT_MARGIN)}px`,
    "max-height": `${Math.min((above ? anchor.top : below) - 12, window.innerHeight * 0.45)}px`,
    ...(above
      ? { bottom: `${window.innerHeight - anchor.top + 4}px` }
      : { top: `${anchor.bottom + 4}px` }),
  };
}

/**
 * One axis's options as a keyboard-driven listbox, mounted while open. Keys are read in the window's capture phase so
 * a pick beats the focused composer or dialog; a pointer-down outside `inside` (the menu plus its toggle) or the window
 * losing focus closes it. A null `inside` leaves dismissal to the enclosing modal, which survives a window blur.
 */
export function ControlMenu(props: {
  axis: Pick<AgentControlAxis, "label" | "value"> & { options: readonly ControlMenuOption[] };
  class: string;
  style?: JSX.CSSProperties;
  inside: string | null;
  onPick: (optionId: string) => void;
  onClose: () => void;
}): JSX.Element {
  const nav = createListNavigation({
    count: () => props.axis.options.length,
    edges: "wrap",
    initialIndex: Math.max(
      0,
      props.axis.options.findIndex((option) => option.id === props.axis.value),
    ),
    acceptKeys: ["Enter", "Tab"],
    // An axis with no options has nothing to pick (index -1), so accepting just closes it.
    onAccept: (index) => {
      const option = props.axis.options[index];
      if (option === undefined) props.onClose();
      else if (option.unavailable !== true) props.onPick(option.id);
    },
    onDismiss: props.onClose,
  });
  onMount(() => {
    setOpenMenus((count) => count + 1);
    window.addEventListener("keydown", nav.onKeyDown, { capture: true });
    nav.reveal("nearest");
  });
  onCleanup(() => {
    setOpenMenus((count) => count - 1);
    window.removeEventListener("keydown", nav.onKeyDown, { capture: true });
  });
  if (props.inside !== null) {
    dismissOnOutsideInteraction(props.inside, props.onClose);
  }

  return (
    <div
      class={`control-menu ${props.class}`}
      style={props.style}
      role="listbox"
      aria-label={props.axis.label}
    >
      {/* Redundant to the listbox aria-label; hidden so the listbox has only option children. */}
      <div class="control-menu-head ui-section-label" aria-hidden="true">
        {props.axis.label}
      </div>
      <For each={props.axis.options}>
        {(option, index) => (
          <>
            <Show
              when={
                option.group !== null &&
                (index() === 0 || props.axis.options[index() - 1]?.group !== option.group)
              }
            >
              <div class="control-menu-option-group ui-section-label" aria-hidden="true">
                {option.group}
              </div>
            </Show>
            <div
              {...nav.row(index())}
              class="control-menu-option ui-row ui-row-stack"
              role="option"
              tabindex={-1}
              aria-selected={option.id === props.axis.value}
              aria-disabled={option.unavailable === true}
              classList={{ active: index() === nav.index() }}
              onPointerDown={(event) => {
                event.preventDefault();
                if (option.unavailable !== true) props.onPick(option.id);
              }}
            >
              <span class="ui-row-text">
                <span class="control-menu-option-label">{option.label}</span>
                <Show when={option.description !== null}>
                  <span class="control-menu-option-desc ui-row-desc">{option.description}</span>
                </Show>
              </span>
            </div>
          </>
        )}
      </For>
      <Show when={props.axis.options.length === 0}>
        <div class="control-menu-empty">No options available</div>
      </Show>
    </div>
  );
}
