import { ChevronDown } from "lucide-solid";
import { createEffect, createSignal, type JSX, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { ControlMenu, type ControlMenuOption, controlMenuPlacement } from "../chrome/ControlMenu";

/** One composer field that opens a menu: its label, the value shown, a busy note, and the problem line under it. */
export function NewSessionPicker(props: {
  /** Unique within the composer; names the label and value for assistive tech. */
  id: string;
  label: string;
  value: string;
  /** The picked option's id, highlighted in the menu. */
  selected: string;
  options: readonly ControlMenuOption[];
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
    <div class="session-composer-workflow">
      <span id={`${props.id}-label`}>{props.label}</span>
      <button
        type="button"
        class="session-composer-workflow-toggle ui-field"
        aria-labelledby={`${props.id}-label ${props.id}-value`}
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
        <span id={`${props.id}-value`} class="session-composer-workflow-value">
          {props.value}
        </span>
        <Show when={props.busy}>{(busy) => <span class="ui-row-desc">{busy()}</span>}</Show>
        <span class="ui-icon" aria-hidden="true">
          <ChevronDown />
        </span>
      </button>
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
