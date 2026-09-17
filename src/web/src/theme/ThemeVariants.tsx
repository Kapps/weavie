import { Show } from "solid-js";
import type { ThemePickerModel } from "./picker-model";
import { ThemeChoiceList } from "./ThemeChoiceList";

export function ThemeVariants(props: { model: ThemePickerModel; onClose: () => void }) {
  const m = props.model;
  return (
    <section class="theme-picker-variants" data-theme-pane="variants" aria-label="Theme variants">
      <Show
        when={m.activeExtension()}
        fallback={<p>Choose a theme package to preview its variants here.</p>}
      >
        {(extension) => (
          <>
            <div class="theme-picker-header">
              <strong>{extension().displayName ?? extension().name}</strong>
              <button
                type="button"
                disabled={m.saving()}
                onClick={props.onClose}
                aria-label="Close variants"
              >
                ×
              </button>
            </div>
            <Show when={m.packageLoading()}>
              <p role="status">Loading variants…</p>
            </Show>
            <Show when={m.packageError()}>
              <p class="theme-picker-error" role="alert">
                {m.packageError()}
              </p>
            </Show>
            <Show when={m.variants().length > 0}>
              <input
                id="theme-variant-filter"
                aria-label="Filter theme variants"
                role="combobox"
                aria-expanded="true"
                aria-controls="theme-variants"
                aria-activedescendant={
                  m.variantChoices().length ? `theme-variants-${m.variantSelected()}` : undefined
                }
                placeholder="Filter variants…"
                value={m.variantQuery()}
                disabled={m.saving()}
                onInput={(event) => m.setVariantQuery(event.currentTarget.value)}
              />
              <ThemeChoiceList
                id="theme-variants"
                label="Theme variants"
                choices={m.variantChoices().map((v) => v.choice)}
                selected={m.variantSelected()}
                savedId={m.savedId}
                disabled={m.saving()}
                onPreview={m.highlightVariant}
                onApply={() => void m.acceptVariant()}
              />
              <small>Hover or ↑ ↓ to preview · Enter to install · ← to results</small>
              <button
                type="button"
                disabled={m.saving() || m.variantChoices().length === 0}
                onClick={() => void m.acceptVariant()}
              >
                {m.saving() ? "Applying…" : "Install & Apply"}
              </button>
            </Show>
          </>
        )}
      </Show>
    </section>
  );
}
