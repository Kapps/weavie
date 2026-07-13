import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentControlAxis } from "../bridge";
import {
  agentControlState,
  closeControlPicker,
  openControlAxis,
  setAgentControl,
} from "./agent-controls-store";

// The popover that opens above a status-line segment (or from a `/model`-style command): the axis's options,
// keyboard-navigable. AgentStatusLine owns the `agentControlPickerOpen` gate (whenever any picker is open) so the
// composer's Enter/Escape commands stand down and this window handler drives selection instead.
export function AgentControlPicker(props: { backendId: string; slot: string | null }): JSX.Element {
  const [highlight, setHighlight] = createSignal(0);
  const axis = createMemo<AgentControlAxis | null>(() => {
    const id = openControlAxis();
    if (id === null || props.slot === null) {
      return null;
    }
    return agentControlState(props.slot).axes.find((candidate) => candidate.id === id) ?? null;
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
      // A re-push can shrink the options while open; keep the highlight in range without re-seeding it.
      setHighlight((index) => Math.min(index, Math.max(0, current.options.length - 1)));
      return;
    }
    seededAxis = current.id;
    const index = current.options.findIndex((option) => option.id === current.value);
    setHighlight(index >= 0 ? index : 0);
  });

  const pick = (optionId: string): void => {
    const slot = props.slot;
    const current = axis();
    if (slot !== null && current !== null) {
      setAgentControl(props.backendId, slot, current.id, optionId);
    }
    closeControlPicker();
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    const current = axis();
    if (current === null || current.options.length === 0) {
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        closeControlPicker();
      }
      return;
    }
    if (event.key === "ArrowDown") {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((index) => (index + 1) % current.options.length);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((index) => (index <= 0 ? current.options.length - 1 : index - 1));
    } else if (event.key === "Enter" || event.key === "Tab") {
      event.preventDefault();
      event.stopPropagation();
      pick(current.options[highlight()]?.id ?? current.value);
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      closeControlPicker();
    }
  };

  // Only listen while open, in capture phase so the pick beats the composer's own history/keydown handling.
  createEffect(() => {
    if (axis() === null) {
      return;
    }
    window.addEventListener("keydown", onKeyDown, { capture: true });
    onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));
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
                classList={{ active: index() === highlight() }}
                onMouseEnter={() => setHighlight(index())}
                onPointerDown={(event) => {
                  event.preventDefault();
                  pick(option.id);
                }}
              >
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
