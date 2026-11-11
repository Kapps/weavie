import { ChevronDown } from "lucide-solid";
import { createSignal, For, type JSX, Show } from "solid-js";
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
  const [open, setOpen] = createSignal<{ axis: AgentControlAxis; anchor: DOMRect } | null>(null);
  const toggle = (axis: AgentControlAxis, segment: HTMLElement): void => {
    setOpen((current) =>
      current?.axis.id === axis.id ? null : { axis, anchor: segment.getBoundingClientRect() },
    );
  };

  return (
    <>
      <fieldset class="control-bar" aria-label={props.label} disabled={props.disabled}>
        <For each={props.axes}>
          {(axis) => (
            <button
              type="button"
              class="control-bar-pick"
              disabled={props.busy}
              aria-haspopup="listbox"
              aria-expanded={open()?.axis.id === axis.id}
              title={`${axis.label}: ${axis.valueLabel}`}
              onClick={(event) => toggle(axis, event.currentTarget)}
              onKeyDown={(event) => {
                if (event.key === "ArrowDown" && open() === null) {
                  event.preventDefault();
                  toggle(axis, event.currentTarget);
                }
              }}
            >
              <small class="ui-section-label">{axis.label}</small>
              <span>
                {axis.valueLabel}
                <ChevronDown size="0.95em" aria-hidden="true" />
              </span>
            </button>
          )}
        </For>
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
        {(menu) => (
          <Portal>
            <ControlMenu
              axis={menu.axis}
              class={props.menuClass}
              style={controlMenuPlacement(menu.anchor)}
              inside={`.${props.menuClass}, .control-bar-pick`}
              onPick={(value) => {
                setOpen(null);
                props.onPick(menu.axis.id, value);
              }}
              onClose={() => setOpen(null)}
            />
          </Portal>
        )}
      </Show>
    </>
  );
}
