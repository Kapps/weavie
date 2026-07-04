import { type JSX, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";

/**
 * Non-dismissible full-UI blocker shown from the update-restart commit until the new worker is back
 * (or the tab reloads onto the new assets). The host has already frozen terminal input; the
 * capture-phase key swallow + focus steal make the page match it.
 */
export function UpdateOverlay(): JSX.Element {
  const swallow = (event: KeyboardEvent): void => {
    event.preventDefault();
    event.stopPropagation();
  };
  onMount(() => window.addEventListener("keydown", swallow, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", swallow, { capture: true }));

  return (
    <Portal>
      <div class="update-overlay" role="alertdialog" aria-modal="true" aria-label="Updating Weavie">
        <div
          class="update-overlay-card"
          tabindex="-1"
          ref={(el) => {
            // Pull focus off the terminal so its hidden textarea stops receiving input events.
            queueMicrotask(() => el.focus());
          }}
        >
          <span class="connection-spinner" aria-hidden="true" />
          <span>Updating Weavie — your sessions will reload…</span>
        </div>
      </div>
    </Portal>
  );
}
