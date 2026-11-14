import { For, onCleanup, Show } from "solid-js";
import { ModalShell, PromptButton } from "../chrome/ModalShell";
import { currentThemeType } from "./controller";
import { createThemePicker } from "./picker-model";
import { type ThemePickerState, type ThemeSearchOrder, themePicker } from "./picker-state";
import { ThemeAppearanceSelect } from "./ThemeAppearanceSelect";
import { ThemeChoiceList } from "./ThemeChoiceList";
import { ThemeExtensionDetails } from "./ThemeExtensionDetails";
import { ThemeVariants } from "./ThemeVariants";
import "./theme-picker.css";

export function ThemePicker() {
  return (
    <Show when={themePicker()} keyed>
      {(opened) => <Picker opened={opened} />}
    </Show>
  );
}

function Picker(props: { opened: ThemePickerState }) {
  const m = createThemePicker(props.opened);
  const title = m.target === "editor" ? "Select Editor Theme" : "Select Color Theme";
  // The editor theme is saved per polarity, so say which one Apply sets.
  const mode = m.target === "editor" ? ` (${currentThemeType()} mode)` : "";
  const focusBefore = document.activeElement;
  let input!: HTMLInputElement;
  onCleanup(() => {
    if (focusBefore instanceof HTMLElement && focusBefore.isConnected) focusBefore.focus();
  });
  const focusResult = () => document.getElementById(`theme-options-${m.selected()}`)?.focus();
  async function openExtension(index: number) {
    const source = document.activeElement;
    const opened = await m.openExtension(index);
    if (opened && m.selected() === index && document.activeElement === source) {
      document.getElementById("theme-variant-filter")?.focus();
    }
  }
  const accept = () => (m.registry() ? openExtension(m.selected()) : m.accept());
  const applyLabel = () => (m.saving() ? "Applying…" : m.registry() ? "Preview themes" : "Apply");
  function keys(event: KeyboardEvent) {
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      m.close();
      return;
    }
    if (event.target instanceof HTMLSelectElement || m.saving()) return;
    const target = event.target instanceof HTMLElement ? event.target : null;
    const inVariants = target?.closest('[data-theme-pane="variants"]') !== null && target !== null;
    const count = inVariants ? m.variantChoices().length : m.count();
    const option = target?.getAttribute("role") === "option";
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      event.stopPropagation();
      if (count === 0) return;
      const selected = inVariants ? m.variantSelected() : m.selected();
      const index = (selected + (event.key === "ArrowDown" ? 1 : -1) + count) % count;
      const next = document.getElementById(
        `${inVariants ? "theme-variants" : "theme-options"}-${index}`,
      );
      // A focused option previews on focus, so focus follows the highlight.
      if (option) next?.focus();
      else {
        if (inVariants) m.highlightVariant(index);
        else void m.highlight(index);
        next?.scrollIntoView({ block: "nearest" });
      }
    }
    if (
      event.key === "ArrowLeft" &&
      inVariants &&
      (target?.getAttribute("role") === "option" ||
        (target instanceof HTMLInputElement && target.value === ""))
    ) {
      event.preventDefault();
      event.stopPropagation();
      focusResult();
    }
    if (
      (event.key === "Enter" && (target instanceof HTMLInputElement || option)) ||
      (event.key === "ArrowRight" && !inVariants && m.registry() && option)
    ) {
      event.preventDefault();
      event.stopPropagation();
      if (inVariants) void m.acceptVariant();
      else void accept();
    }
  }

  return (
    <ModalShell
      title={
        <>
          {title}
          <Show when={mode}>
            <small>{mode}</small>
          </Show>
        </>
      }
      actions={
        <>
          <Show when={m.reset}>
            {(reset) => (
              <PromptButton
                label="Reset"
                shortcut=""
                title="Revert the editor to the app theme"
                disabled={m.saving()}
                onClick={() => void reset()()}
              />
            )}
          </Show>
          <PromptButton
            label="Cancel"
            shortcut="Esc"
            title="Cancel (Esc)"
            disabled={m.saving()}
            onClick={m.close}
          />
          <PromptButton
            label={applyLabel()}
            shortcut="Enter"
            title={`${applyLabel()} (Enter)`}
            disabled={m.saving() || m.count() === 0}
            onClick={() => void accept()}
            primary
          />
        </>
      }
      class={`theme-picker${m.registry() ? " theme-picker-registry" : ""}`}
      onDismiss={m.close}
      onKeyDown={keys}
    >
      <div class="theme-picker-sources">
        <button
          type="button"
          class="ui-button"
          aria-pressed={!m.registry()}
          disabled={m.saving()}
          onClick={() => {
            m.switchSource(false);
            input.focus();
          }}
        >
          Installed
        </button>
        <button
          type="button"
          class="ui-button"
          aria-pressed={m.registry()}
          disabled={m.saving()}
          onClick={() => {
            m.switchSource(true);
            input.focus();
          }}
        >
          Open VSX
        </button>
      </div>
      <div class="theme-picker-body">
        <section class="theme-picker-results" aria-label="Theme results">
          <input
            class="ui-field"
            ref={(el) => {
              input = el;
              queueMicrotask(() => el.focus());
            }}
            aria-label={m.registry() ? "Search Open VSX themes" : "Filter themes"}
            placeholder={m.registry() ? "Search Open VSX themes…" : "Type to filter themes…"}
            value={m.query()}
            disabled={m.saving()}
            onInput={(e) => m.setQuery(e.currentTarget.value)}
            role="combobox"
            aria-expanded="true"
            aria-controls="theme-options"
            aria-activedescendant={m.count() ? `theme-options-${m.selected()}` : undefined}
          />
          <Show when={!m.registry()}>
            <ThemeAppearanceSelect model={m} onChosen={focusResult} />
          </Show>
          <Show when={m.registry()}>
            <label class="theme-picker-sort">
              Sort by
              <select
                class="ui-field"
                aria-label="Sort Open VSX themes"
                value={m.sortBy()}
                disabled={m.saving()}
                onChange={(event) => m.setSortBy(event.currentTarget.value as ThemeSearchOrder)}
              >
                <option value="downloadCount">Most downloaded</option>
                <option value="relevance">Relevance</option>
              </select>
            </label>
          </Show>
          <Show
            when={m.registry()}
            fallback={
              <ThemeChoiceList
                id="theme-options"
                label="Color themes"
                choices={m.choices()}
                selected={m.selected()}
                savedId={m.savedId}
                disabled={m.saving()}
                onPreview={(index) => void m.highlight(index)}
                onApply={() => void m.accept()}
              />
            }
          >
            <div
              class="theme-picker-list"
              id="theme-options"
              role="listbox"
              aria-label="Color themes"
            >
              <For each={m.extensions()}>
                {(extension, index) => (
                  <button
                    type="button"
                    class="ui-row ui-row-stack"
                    role="option"
                    id={`theme-options-${index()}`}
                    aria-selected={m.selected() === index()}
                    disabled={m.saving()}
                    aria-controls="theme-variants"
                    title="Preview variants (Enter or →)"
                    onFocus={() => void m.highlight(index())}
                    onClick={() => void openExtension(index())}
                  >
                    <span>{extension.displayName ?? extension.name}</span>
                    <small>v{extension.version}</small>
                    <small class="theme-description">{extension.description}</small>
                    <ThemeExtensionDetails extension={extension} />
                  </button>
                )}
              </For>
              <Show when={!m.searchLoading() && m.count() === 0}>
                <p>No themes found.</p>
              </Show>
            </div>
          </Show>
          <Show when={m.searchLoading()}>
            <p role="status">
              Loading themes… Temporary connection failures are retried automatically.
            </p>
          </Show>
          <Show when={m.registry() && m.extensions().length < m.total()}>
            <button
              type="button"
              class="ui-button"
              disabled={m.searchLoading() || m.saving()}
              onClick={() => void m.loadMore()}
            >
              Load more ({m.extensions().length} of {m.total()})
            </button>
          </Show>
          <small>{m.registry() ? "↑ ↓ Browse · → Open variants" : "↑ ↓ Preview"}</small>
        </section>
        <Show when={m.registry()}>
          <ThemeVariants
            model={m}
            onClose={() => {
              m.closeVariants();
              focusResult();
            }}
          />
        </Show>
      </div>
      <Show when={m.saving() && m.registry()}>
        <p role="status">
          Downloading theme… Temporary connection failures are retried automatically.
        </p>
      </Show>
      <Show when={m.error()}>
        <p class="theme-picker-error" role="alert">
          {m.error()}
        </p>
      </Show>
    </ModalShell>
  );
}
