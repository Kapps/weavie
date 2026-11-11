import { ChevronDown } from "lucide-solid";
import { createSignal, createUniqueId, For, Index, type JSX, Show } from "solid-js";
import { Portal } from "solid-js/web";
import type { AgentControlAxis } from "../messaging/protocol-types";
import { ControlMenu, controlMenuPlacement } from "./ControlMenu";
import "./control-bar.css";

/** One bar of pickers, a segment per axis the agent reports: its label over its value, opening that axis's menu. */
export function ControlBar(props: {
  /** Names the group for assistive tech. */
  label: string;
  /** The pickers; undefined while the agent is being asked, which shows `placeholders` instead. */
  axes: readonly AgentControlAxis[] | undefined;
  placeholders: readonly string[];
  disabled: boolean;
  /** A change is being applied, so no segment can be opened. */
  busy: boolean;
  /** The class the open menu takes, for its stacking order. */
  menuClass: string;
  onPick: (axisId: string, value: string) => void;
}): JSX.Element {
  // The open segment's id and where it sat; a menu and its toggles are told apart from every other bar's by `scope`.
  const [open, setOpen] = createSignal<{ id: string; anchor: DOMRect } | null>(null);
  const scope = createUniqueId();
  // A busy segment stays focusable, so the focus survives the change it is waiting on; it just doesn't open.
  const toggle = (id: string, segment: HTMLElement): void => {
    if (props.busy) {
      return;
    }
    setOpen((current) =>
      current?.id === id ? null : { id, anchor: segment.getBoundingClientRect() },
    );
  };

  return (
    <>
      <fieldset
        class="control-bar"
        aria-label={props.label}
        disabled={props.disabled}
        data-control-bar={scope}
      >
        {/* By position, so a segment whose value changed stays the same button and keeps the focus. */}
        <Index each={props.axes}>
          {(axis) => (
            <button
              type="button"
              class="control-bar-pick"
              // Nothing is chosen yet when the value is none of the options: a choice still to be made.
              classList={{
                "control-bar-unset": !axis().options.some((option) => option.id === axis().value),
              }}
              aria-disabled={props.busy}
              aria-haspopup="listbox"
              aria-expanded={open()?.id === axis().id}
              title={`${axis().label}: ${axis().valueLabel}`}
              onClick={(event) => toggle(axis().id, event.currentTarget)}
              onKeyDown={(event) => {
                if (event.key === "ArrowDown" && open() === null) {
                  event.preventDefault();
                  toggle(axis().id, event.currentTarget);
                }
              }}
            >
              <small class="ui-section-label">{axis().label}</small>
              <span>
                {axis().valueLabel}
                <ChevronDown size="0.95em" aria-hidden="true" />
              </span>
            </button>
          )}
        </Index>
        <Show when={props.axes === undefined}>
          <For each={props.placeholders}>
            {(label) => (
              <span
                class="control-bar-pick control-bar-placeholder"
                aria-busy="true"
                title={`${label}: asking the agent…`}
              >
                <small class="ui-section-label">{label}</small>
                <i />
              </span>
            )}
          </For>
        </Show>
      </fieldset>
      <Show when={open()} keyed>
        {(at) => (
          <Show when={props.axes?.find((candidate) => candidate.id === at.id)}>
            {(axis) => (
              <Portal>
                <ControlMenu
                  axis={axis()}
                  class={`${props.menuClass} control-bar-menu-${scope}`}
                  style={controlMenuPlacement(at.anchor)}
                  inside={`.control-bar-menu-${scope}, [data-control-bar="${scope}"] .control-bar-pick`}
                  onPick={(value) => {
                    setOpen(null);
                    props.onPick(at.id, value);
                  }}
                  onClose={() => setOpen(null)}
                />
              </Portal>
            )}
          </Show>
        )}
      </Show>
    </>
  );
}
