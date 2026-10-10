import { For, type JSX, Show } from "solid-js";
import { ModalShell, modalSubmitKeys } from "../chrome/ModalShell";
import { backgroundStopRequest } from "./background-guard";
import { formatElapsed } from "./turn-progress";

/** Confirms an action that would stop running subagents or background tasks; Enter and Esc keep the work. */
export function BackgroundStopPrompt(): JSX.Element {
  return (
    <Show when={backgroundStopRequest()}>
      {(request) => (
        <ModalShell
          labelledBy="background-stop-title"
          onDismiss={() => request().settle(false)}
          onKeyDown={modalSubmitKeys(
            () => request().settle(false),
            () => request().settle(false),
          )}
        >
          <div class="confirm-title" id="background-stop-title">
            Stop background work?
          </div>
          <div class="confirm-body">
            <div>
              Background work is still running. Stopping it ends that work, and its results won't
              come back.
            </div>
            <ul class="confirm-file-list">
              <For each={request().work}>
                {(item) => (
                  <li>
                    {item.name} — {item.type}, {formatElapsed(Date.now() - item.startedAtMs)}
                  </li>
                )}
              </For>
            </ul>
          </div>
          <div class="confirm-actions">
            <button type="button" class="confirm-btn" onClick={() => request().settle(true)}>
              Close anyway
            </button>
            <button
              type="button"
              class="confirm-btn confirm-btn-primary"
              ref={(element) => queueMicrotask(() => element.focus())}
              onClick={() => request().settle(false)}
            >
              Keep session
            </button>
          </div>
        </ModalShell>
      )}
    </Show>
  );
}
