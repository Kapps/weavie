import { For, type JSX, Show } from "solid-js";
import { ConfirmDialog } from "../editor/ConfirmDialog";
import { backgroundStopRequest } from "./background-guard";
import { formatElapsed } from "./turn-progress";

/** Confirms an action that would stop running subagents or background tasks. Esc keeps the work running. */
export function BackgroundStopPrompt(): JSX.Element {
  return (
    <Show when={backgroundStopRequest()}>
      {(request) => (
        <ConfirmDialog
          title="Stop background work?"
          body={
            <>
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
            </>
          }
          confirmLabel="Close anyway"
          cancelLabel="Keep session"
          onConfirm={() => request().settle(true)}
          onCancel={() => request().settle(false)}
        />
      )}
    </Show>
  );
}
