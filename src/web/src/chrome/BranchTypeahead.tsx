import { For, type JSX, Show, createSignal, onCleanup, onMount } from "solid-js";

/** Existing branches containing the typed text (case-insensitive), minus an exact full match; capped. */
export function branchSuggestions(branches: string[], typed: string): string[] {
  const q = typed.trim().toLowerCase();
  if (q.length === 0) {
    return [];
  }
  return branches.filter((b) => b.toLowerCase().includes(q) && b !== typed.trim()).slice(0, 8);
}

// The branch/ref typeahead shared by the session and diff-against prompts: a combobox input suggesting the
// given branches as you type, with window-capture keys — ↑/↓ walk the suggestions, Enter submits (the
// highlighted branch, else the typed text), Esc cancels. The value lives in the parent so it can drive its
// own action buttons; a suggestion pick (pointer or arrowed Enter) reports viaPick=true so an existing
// branch can act differently from a typed name.
export function BranchTypeahead(props: {
  idPrefix: string;
  placeholder: string;
  ariaLabel: string;
  branches: string[];
  value: string;
  setValue: (value: string) => void;
  onSubmit: (text: string, shiftKey: boolean, viaPick: boolean) => void;
  onCancel: () => void;
}): JSX.Element {
  const [highlight, setHighlight] = createSignal(-1);
  const suggestions = (): string[] => branchSuggestions(props.branches, props.value);

  const onKeyDown = (event: KeyboardEvent): void => {
    const list = suggestions();
    if (event.key === "ArrowDown" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h + 1) % list.length);
    } else if (event.key === "ArrowUp" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h <= 0 ? list.length - 1 : h - 1));
    } else if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      const picked = highlight() >= 0 && highlight() < list.length ? list[highlight()] : undefined;
      if (picked !== undefined) {
        props.onSubmit(picked, event.shiftKey, true);
      } else {
        props.onSubmit(props.value.trim(), event.shiftKey, false);
      }
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onCancel();
    }
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  return (
    <>
      <input
        class="session-prompt-input"
        type="text"
        placeholder={props.placeholder}
        role="combobox"
        aria-label={props.ariaLabel}
        aria-autocomplete="list"
        aria-expanded={suggestions().length > 0}
        aria-controls={suggestions().length > 0 ? `${props.idPrefix}-suggestions` : undefined}
        aria-activedescendant={
          highlight() >= 0 ? `${props.idPrefix}-opt-${highlight()}` : undefined
        }
        spellcheck={false}
        autocomplete="off"
        value={props.value}
        onInput={(event) => {
          props.setValue(event.currentTarget.value);
          setHighlight(-1);
        }}
        ref={(el) => {
          queueMicrotask(() => el.focus());
        }}
      />
      <Show when={suggestions().length > 0}>
        <div
          class="session-prompt-suggestions"
          id={`${props.idPrefix}-suggestions`}
          role="listbox"
          aria-label="Matching branches"
        >
          <For each={suggestions()}>
            {(name, i) => (
              <div
                class="session-prompt-suggestion"
                role="option"
                tabindex={-1}
                id={`${props.idPrefix}-opt-${i()}`}
                aria-selected={i() === highlight()}
                classList={{ active: i() === highlight() }}
                // pointerdown (not click) so picking a suggestion isn't lost to the input's blur, and
                // preventDefault keeps focus in the field.
                onPointerDown={(event) => {
                  event.preventDefault();
                  props.onSubmit(name, false, true);
                }}
              >
                {name}
              </div>
            )}
          </For>
        </div>
      </Show>
    </>
  );
}
