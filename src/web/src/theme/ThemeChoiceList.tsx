import { For, Show } from "solid-js";
import type { ThemeChoice } from "./picker-state";

export function ThemeChoiceList(props: {
  id: string;
  label: string;
  choices: ThemeChoice[];
  selected: number;
  savedId: string;
  disabled: boolean;
  onPreview: (index: number) => void;
  onApply: () => void;
}) {
  return (
    <div class="theme-picker-list" id={props.id} role="listbox" aria-label={props.label}>
      <For each={props.choices}>
        {(choice, index) => (
          <button
            type="button"
            role="option"
            id={`${props.id}-${index()}`}
            aria-selected={props.selected === index()}
            disabled={props.disabled}
            onPointerMove={() => {
              if (props.selected !== index()) props.onPreview(index());
            }}
            onFocus={() => props.onPreview(index())}
            onClick={() => {
              props.onPreview(index());
              props.onApply();
            }}
          >
            <span>
              {choice.label}
              {choice.id === props.savedId ? " ✓" : ""}
            </span>
            <small>
              {choice.type} · {choice.namespace ?? "Built-in"}
            </small>
          </button>
        )}
      </For>
      <Show when={props.choices.length === 0}>
        <p>No themes found.</p>
      </Show>
    </div>
  );
}
