import { createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { AgentControlAxis } from "../bridge";
import { createListNavigation } from "../list-navigation";
import { dismissOnOutsideInteraction } from "./popover-dismiss";
import "./control-menu.css";

const [openMenus, setOpenMenus] = createSignal(0);

/** Whether a control menu is open, so an enclosing dialog leaves Enter and Escape to it. */
export const controlMenuOpen = (): boolean => openMenus() > 0;

/**
 * One axis's options as a keyboard-driven listbox, mounted while open. Keys are read in the window's capture phase so
 * a pick beats the focused composer or dialog; a pointer-down outside `inside` (the menu plus its toggle) closes it.
 */
export function ControlMenu(props: {
  axis: AgentControlAxis;
  class: string;
  style?: JSX.CSSProperties;
  inside: string;
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
      else props.onPick(option.id);
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
  dismissOnOutsideInteraction(props.inside, props.onClose);

  return (
    <div
      class={`control-menu ${props.class}`}
      style={props.style}
      role="listbox"
      aria-label={props.axis.label}
    >
      {/* Redundant to the listbox aria-label; hidden so the listbox has only option children. */}
      <div class="control-menu-head" aria-hidden="true">
        {props.axis.label}
      </div>
      <For each={props.axis.options}>
        {(option, index) => (
          <div
            {...nav.row(index())}
            class="control-menu-option"
            role="option"
            tabindex={-1}
            aria-selected={option.id === props.axis.value}
            classList={{ active: index() === nav.index() }}
            onPointerDown={(event) => {
              event.preventDefault();
              props.onPick(option.id);
            }}
          >
            <Show
              when={
                option.group !== null &&
                (index() === 0 || props.axis.options[index() - 1]?.group !== option.group)
              }
            >
              <span class="control-menu-option-group">{option.group}</span>
            </Show>
            <span class="control-menu-option-label">{option.label}</span>
            <Show when={option.description !== null}>
              <span class="control-menu-option-desc">{option.description}</span>
            </Show>
          </div>
        )}
      </For>
      <Show when={props.axis.options.length === 0}>
        <div class="control-menu-empty">No options available</div>
      </Show>
    </div>
  );
}
