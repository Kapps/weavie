import { Check } from "lucide-solid";
import { createSignal, Index, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { controlMenuPlacement } from "../chrome/ControlMenu";
import { dismissOnOutsideInteraction } from "../chrome/popover-dismiss";
import "../chrome/control-menu.css";
import { type ColumnFilter, filterActive, noFilter } from "./agent-table-model";

/**
 * A column's filter, under its header: a phrase, and the column's most common values to pick from. It is open only
 * while it holds the user's attention: a press or focus anywhere else, Escape, or its column leaving view closes it.
 */
export function AgentTableFilter(props: {
  column: string;
  anchor: HTMLElement;
  filter: ColumnFilter;
  common: readonly { value: string; count: number }[];
  onChange: (filter: ColumnFilter) => void;
  onClose: () => void;
}): JSX.Element {
  let panel: HTMLDivElement | undefined;
  let field: HTMLInputElement | undefined;
  dismissOnOutsideInteraction(
    '.agent-table-filter, .agent-table-filter-toggle[aria-expanded="true"]',
    props.onClose,
  );
  // The panel is fixed, so it is placed again whenever its column can have moved: a scroll, a resize, or the
  // transcript growing, which moves a bottom-anchored transcript without a scroll event.
  const [place, setPlace] = createSignal(props.anchor.getBoundingClientRect());
  onMount(() => {
    const view = props.anchor.closest(".agent-transcript-scroller");
    const follow = (event?: Event): void => {
      // The panel's own list scrolling moves nothing.
      if (event?.target instanceof Node && panel?.contains(event.target) === true) return;
      const next = props.anchor.getBoundingClientRect();
      const bounds = view?.getBoundingClientRect();
      if (bounds !== undefined && (next.bottom < bounds.top || next.top > bounds.bottom)) {
        props.onClose();
      } else {
        setPlace(next);
      }
    };
    const grown = new ResizeObserver(() => follow());
    const extent = props.anchor.closest(".agent-transcript-extent");
    if (extent !== null) grown.observe(extent);
    window.addEventListener("scroll", follow, true);
    window.addEventListener("resize", follow);
    // In the capture phase, so Escape closes the panel even when a removed row left focus on the document.
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      props.anchor.focus();
      props.onClose();
    };
    window.addEventListener("keydown", onKeyDown, true);
    const onFocusIn = (event: FocusEvent): void => {
      const target = event.target as Node | null;
      if (panel?.contains(target) !== true && !props.anchor.contains(target)) props.onClose();
    };
    // After the opening click, which the pane answers by focusing its composer.
    queueMicrotask(() => {
      if (field?.isConnected !== true) return;
      field.focus();
      document.addEventListener("focusin", onFocusIn);
    });
    onCleanup(() => {
      grown.disconnect();
      window.removeEventListener("scroll", follow, true);
      window.removeEventListener("resize", follow);
      window.removeEventListener("keydown", onKeyDown, true);
      document.removeEventListener("focusin", onFocusIn);
    });
  });
  const toggle = (value: string): void =>
    props.onChange({
      ...props.filter,
      values: props.filter.values.includes(value)
        ? props.filter.values.filter((picked) => picked !== value)
        : [...props.filter.values, value],
    });
  return (
    <Portal>
      <div
        ref={panel}
        class="control-menu agent-table-filter"
        style={controlMenuPlacement(place())}
        role="dialog"
        aria-label={`Filter ${props.column}`}
      >
        <input
          ref={field}
          class="ui-field"
          type="search"
          placeholder="Contains"
          aria-label={`${props.column} contains`}
          value={props.filter.phrase}
          onInput={(event) =>
            props.onChange({ ...props.filter, phrase: event.currentTarget.value })
          }
        />
        {/* By index, so an update recounts the rows in place; a press keeps focus in the field. */}
        <Index each={props.common}>
          {(entry) => (
            <button
              type="button"
              class="ui-row control-menu-option"
              aria-pressed={props.filter.values.includes(entry().value)}
              onPointerDown={(event) => event.preventDefault()}
              onClick={() => toggle(entry().value)}
            >
              <span class="ui-icon agent-table-filter-check">
                <Show when={props.filter.values.includes(entry().value)}>
                  <Check />
                </Show>
              </span>
              <span class="agent-table-filter-value">{entry().value || "(empty)"}</span>
              <span class="ui-row-end ui-row-desc">{entry().count}</span>
            </button>
          )}
        </Index>
        <Show when={filterActive(props.filter)}>
          <button
            type="button"
            class="ui-row"
            onPointerDown={(event) => event.preventDefault()}
            onClick={() => {
              // The button leaves with the filter; from the keyboard, focus would leave the panel with it.
              field?.focus();
              props.onChange(noFilter);
            }}
          >
            Clear Filter
          </button>
        </Show>
      </div>
    </Portal>
  );
}
