import { createEffect, createMemo, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentControlAxis, ClientSession } from "../bridge";
import { dismissOnOutsideInteraction } from "../chrome/popover-dismiss";
import { createListNavigation } from "../list-navigation";
import {
  agentControlState,
  closeControlPicker,
  openControlAxis,
  setAgentControl,
} from "./agent-controls-store";

// The popover that opens above a status-line segment (or from a `/model`-style command): the axis's options,
// keyboard-navigable. AgentStatusLine owns the `agentControlPickerOpen` gate (whenever any picker is open) so the
// composer's Enter/Escape commands stand down and this window handler drives selection instead.
export function AgentControlPicker(props: { session: ClientSession | null }): JSX.Element {
  const axis = createMemo<AgentControlAxis | null>(() => {
    const id = openControlAxis();
    if (id === null || props.session === null) {
      return null;
    }
    return agentControlState(props.session).axes.find((candidate) => candidate.id === id) ?? null;
  });
  // clampIndex keeps the highlight in range when a host re-push shrinks the options while the picker is open.
  const nav = createListNavigation({
    count: () => axis()?.options.length ?? 0,
    edges: "wrap",
    initialIndex: 0,
    acceptKeys: ["Enter", "Tab"],
    onAccept: (index) => {
      const current = axis();
      if (current !== null) {
        pick(current.options[index]?.id ?? current.value);
      }
    },
    onDismiss: closeControlPicker,
    stopPropagation: true,
    clampIndex: true,
  });

  // Seed the highlight only when the picker opens or switches axes: a host re-push rebuilds the axes with
  // fresh references, which would otherwise re-run this and snap keyboard navigation back mid-use.
  let seededAxis: string | null = null;
  createEffect(() => {
    const current = axis();
    if (current === null) {
      seededAxis = null;
      return;
    }
    if (current.id === seededAxis) {
      return;
    }
    seededAxis = current.id;
    const index = current.options.findIndex((option) => option.id === current.value);
    nav.setIndex(index >= 0 ? index : 0);
  });

  const pick = (optionId: string): void => {
    const session = props.session;
    const current = axis();
    if (session !== null && current !== null) {
      setAgentControl(session, current.id, optionId);
    }
    closeControlPicker();
  };

  // An axis with no options still dismisses, but leaves every other key to whatever is behind the picker.
  const onKeyDown = (event: KeyboardEvent): void => {
    if ((axis()?.options.length ?? 0) > 0 || event.key === "Escape") {
      nav.onKeyDown(event);
    }
  };

  // Only listen while open, in capture phase so the pick beats the composer's own history/keydown handling.
  // Anything outside the picker and its status-line segment dismisses it — the segment owns its own toggle.
  createEffect(() => {
    if (axis() === null) {
      return;
    }
    window.addEventListener("keydown", onKeyDown, { capture: true });
    onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));
    dismissOnOutsideInteraction(".agent-control-picker, .agent-status-axis", closeControlPicker);
  });

  return (
    <Show when={axis()}>
      {(current) => (
        <div class="agent-control-picker" role="listbox" aria-label={current().label}>
          {/* Redundant to the listbox aria-label; hidden so the listbox has only option children. */}
          <div class="agent-control-picker-head" aria-hidden="true">
            {current().label}
          </div>
          <For each={current().options}>
            {(option, index) => (
              <div
                class="agent-control-option"
                role="option"
                tabindex={-1}
                aria-selected={option.id === current().value}
                classList={{ active: index() === nav.index() }}
                onMouseEnter={() => nav.setIndex(index())}
                onPointerDown={(event) => {
                  event.preventDefault();
                  pick(option.id);
                }}
              >
                <Show
                  when={
                    option.group !== null &&
                    (index() === 0 || current().options[index() - 1]?.group !== option.group)
                  }
                >
                  <span class="agent-control-option-group">{option.group}</span>
                </Show>
                <span class="agent-control-option-label">{option.label}</span>
                <Show when={option.description !== null}>
                  <span class="agent-control-option-desc">{option.description}</span>
                </Show>
              </div>
            )}
          </For>
          <Show when={current().options.length === 0}>
            <div class="agent-control-empty">No options available</div>
          </Show>
        </div>
      )}
    </Show>
  );
}
