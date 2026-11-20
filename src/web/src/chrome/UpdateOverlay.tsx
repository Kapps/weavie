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
      <div class="update-overlay ui-scrim">
        <div
          class="confirm-dialog ui-card"
          role="alertdialog"
          aria-modal="true"
          aria-labelledby="update-title"
          aria-describedby="update-description"
          tabindex="-1"
          ref={(el) => {
            // Pull focus off the terminal so its hidden textarea stops receiving input events.
            queueMicrotask(() => el.focus());
          }}
        >
          <h2 class="ui-section-label ui-card-tag" id="update-title">
            Updating Weavie
          </h2>
          <div class="update-description" id="update-description">
            <span class="ui-icon" aria-hidden="true">
              <span class="connection-spinner" />
            </span>
            Your sessions will reload.
          </div>
        </div>
      </div>
    </Portal>
  );
}
