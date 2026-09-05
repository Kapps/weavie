import { For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { createListNavigation } from "../list-navigation";

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
  const suggestions = (): string[] => branchSuggestions(props.branches, props.value);
  // Starts with nothing highlighted (-1), which is what makes Enter submit the typed text until an arrow
  // picks a suggestion.
  const nav = createListNavigation({
    count: () => suggestions().length,
    edges: "wrap",
    initialIndex: -1,
    acceptKeys: ["Enter"],
    onAccept: (index, event) => {
      const picked = suggestions()[index];
      props.onSubmit(picked ?? props.value.trim(), event.shiftKey, picked !== undefined);
    },
    onDismiss: () => props.onCancel(),
  });
  onMount(() => window.addEventListener("keydown", nav.onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", nav.onKeyDown, { capture: true }));

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
          nav.index() >= 0 ? `${props.idPrefix}-opt-${nav.index()}` : undefined
        }
        spellcheck={false}
        autocomplete="off"
        value={props.value}
        onInput={(event) => {
          props.setValue(event.currentTarget.value);
          nav.setIndex(-1);
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
                {...nav.row(i())}
                class="session-prompt-suggestion"
                role="option"
                tabindex={-1}
                id={`${props.idPrefix}-opt-${i()}`}
                aria-selected={i() === nav.index()}
                classList={{ active: i() === nav.index() }}
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
