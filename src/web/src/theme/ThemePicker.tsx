import { For, onCleanup, Show } from "solid-js";
import { ModalShell } from "../chrome/ModalShell";
import { keyHint } from "../commands/key-hint";
import { createThemePicker } from "./picker-model";
import { SELECT_THEME, themePickerOpen } from "./picker-state";
import "./theme-picker.css";

export function ThemePicker() {
  return (
    <Show when={themePickerOpen()}>
      <Picker />
    </Show>
  );
}

function Picker() {
  const {
    savedId,
    query,
    setQuery,
    registry,
    extensions,
    variants,
    setVariants,
    total,
    selected,
    error,
    loading,
    saving,
    choices,
    isSearch,
    count,
    close,
    highlight,
    accept,
    switchSource,
    loadMore,
  } = createThemePicker();
  const focusBefore = document.activeElement;
  let input!: HTMLInputElement;
  onCleanup(() => {
    if (focusBefore instanceof HTMLElement && focusBefore.isConnected) focusBefore.focus();
  });
  function keys(event: KeyboardEvent) {
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      close();
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      event.stopPropagation();
      if (saving() || count() === 0) return;
      const index = (selected() + (event.key === "ArrowDown" ? 1 : -1) + count()) % count();
      void highlight(index);
      document.getElementById(`theme-option-${index}`)?.scrollIntoView({ block: "nearest" });
    }
    if (
      event.key === "Enter" &&
      (event.target === input ||
        (event.target instanceof HTMLElement && event.target.getAttribute("role") === "option"))
    ) {
      event.preventDefault();
      event.stopPropagation();
      void accept();
    }
  }

  return (
    <ModalShell
      labelledBy="theme-picker-title"
      class="theme-picker"
      onDismiss={close}
      onKeyDown={keys}
    >
      <div class="theme-picker-header">
        <strong id="theme-picker-title" title={`Select Color Theme${keyHint(SELECT_THEME)}`}>
          Select Color Theme
        </strong>
        <span>{keyHint(SELECT_THEME)}</span>
      </div>
      <div class="theme-picker-sources">
        <button
          type="button"
          aria-pressed={!registry()}
          disabled={saving()}
          onClick={() => {
            switchSource(false);
            input.focus();
          }}
        >
          Installed
        </button>
        <button
          type="button"
          aria-pressed={registry()}
          disabled={saving()}
          onClick={() => {
            switchSource(true);
            input.focus();
          }}
        >
          Open VSX
        </button>
      </div>
      <Show when={variants() !== null}>
        <button
          type="button"
          class="theme-picker-back"
          disabled={saving()}
          onClick={() => setVariants(null)}
        >
          ← Back to results
        </button>
      </Show>
      <input
        ref={(el) => {
          input = el;
          queueMicrotask(() => el.focus());
        }}
        aria-label={isSearch() ? "Search Open VSX themes" : "Filter themes"}
        placeholder={isSearch() ? "Search Open VSX themes…" : "Type to filter themes…"}
        value={query()}
        disabled={saving()}
        readOnly={variants() !== null}
        onInput={(e) => setQuery(e.currentTarget.value)}
        role="combobox"
        aria-expanded="true"
        aria-controls="theme-options"
        aria-activedescendant={count() ? `theme-option-${selected()}` : undefined}
      />
      <div class="theme-picker-list" id="theme-options" role="listbox" aria-label="Color themes">
        <Show
          when={isSearch()}
          fallback={
            <For each={choices()}>
              {(choice, index) => (
                <button
                  type="button"
                  role="option"
                  id={`theme-option-${index()}`}
                  aria-selected={selected() === index()}
                  disabled={saving()}
                  onPointerMove={() => {
                    if (selected() !== index()) void highlight(index());
                  }}
                  onFocus={() => void highlight(index())}
                  onClick={() => {
                    void highlight(index());
                    void accept();
                  }}
                >
                  <span>
                    {choice.label}
                    {choice.id === savedId ? " ✓" : ""}
                  </span>
                  <small>
                    {choice.type} · {choice.namespace ?? "Built-in"}
                  </small>
                </button>
              )}
            </For>
          }
        >
          <For each={extensions()}>
            {(extension, index) => (
              <button
                type="button"
                role="option"
                id={`theme-option-${index()}`}
                aria-selected={selected() === index()}
                onFocus={() => void highlight(index())}
                onClick={() => {
                  void highlight(index());
                  void accept();
                }}
              >
                <span>{extension.displayName ?? extension.name}</span>
                <small>
                  {extension.namespace} · {extension.version}
                </small>
                <small class="theme-description">{extension.description}</small>
              </button>
            )}
          </For>
        </Show>
        <Show when={!loading() && count() === 0}>
          <p>No themes found.</p>
        </Show>
      </div>
      <Show when={error()}>
        <p class="theme-picker-error" role="alert">
          {error()}
        </p>
      </Show>
      <Show when={loading()}>
        <p role="status">Loading themes…</p>
      </Show>
      <Show when={isSearch() && extensions().length < total()}>
        <button type="button" disabled={loading()} onClick={() => void loadMore()}>
          Load more ({extensions().length} of {total()})
        </button>
      </Show>
      <div class="theme-picker-footer">
        <small>
          {isSearch()
            ? "Choose an extension to preview its themes"
            : "↑ ↓ Preview · Enter Apply · Esc Cancel"}
        </small>
        <button type="button" disabled={saving()} onClick={close}>
          Cancel
        </button>
        <button
          type="button"
          class="theme-picker-apply"
          disabled={loading() || saving() || count() === 0}
          onClick={() => void accept()}
        >
          {saving()
            ? "Applying…"
            : isSearch()
              ? "Preview themes"
              : variants() !== null
                ? "Install & Apply"
                : "Apply"}
        </button>
      </div>
    </ModalShell>
  );
}
