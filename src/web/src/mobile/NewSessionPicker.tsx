import { ChevronDown } from "lucide-solid";
import { createEffect, createSignal, type JSX, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { ControlMenu, type ControlMenuOption, controlMenuPlacement } from "../chrome/ControlMenu";

/** A composer menu field with its current value, busy status, and problem line. */
export function NewSessionPicker(props: {
  label: string;
  value: string;
  /** The picked option's id, highlighted in the menu. */
  selected: string;
  options: readonly ControlMenuOption[];
  action: JSX.Element;
  busy: string | null;
  disabled: boolean;
  problem: string | null;
  open: boolean;
  onFocus: () => void;
  onBlur: () => void;
  onPick: (id: string) => void;
}): JSX.Element {
  const [anchor, setAnchor] = createSignal<DOMRect | null>(null);
  createEffect(() => {
    if (!props.open) setAnchor(null);
  });

  return (
    <div class="session-composer-field session-composer-workflow">
      <button
        type="button"
        class="session-composer-workflow-toggle ui-field"
        aria-label={`${props.label} ${props.value}`}
        aria-haspopup="listbox"
        aria-expanded={anchor() !== null}
        aria-busy={props.busy !== null}
        disabled={props.disabled}
        onFocus={() => props.onFocus()}
        onBlur={() => props.onBlur()}
        onClick={(event) =>
          setAnchor((open) => (open === null ? event.currentTarget.getBoundingClientRect() : null))
        }
        onKeyDown={(event) => {
          if (event.key === "ArrowDown" && anchor() === null) {
            event.preventDefault();
            setAnchor(event.currentTarget.getBoundingClientRect());
          }
        }}
      >
        <span class="session-composer-workflow-value">{props.value}</span>
        <Show when={props.busy}>{(busy) => <span class="ui-row-desc">{busy()}</span>}</Show>
        <span class="ui-icon" aria-hidden="true">
          <ChevronDown />
        </span>
      </button>
      {props.action}
      <Show when={props.problem}>{(problem) => <small role="alert">{problem()}</small>}</Show>
      <Show when={anchor()} keyed>
        {(rect) => (
          <Portal>
            <ControlMenu
              axis={{ label: props.label, value: props.selected, options: props.options }}
              class="session-composer-workflow-menu"
              // As wide as the field, so a long description wraps instead of running off a phone.
              style={{ ...controlMenuPlacement(rect), "max-width": `${rect.width}px` }}
              inside=".session-composer-workflow-menu, .session-composer-workflow-toggle"
              onPick={(id) => {
                setAnchor(null);
                props.onPick(id);
              }}
              onClose={() => setAnchor(null)}
            />
          </Portal>
        )}
      </Show>
    </div>
  );
}
