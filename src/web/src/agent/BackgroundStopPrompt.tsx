import { createEffect, createSignal, For, type JSX, Show } from "solid-js";
import { ModalShell, modalSubmitKeys } from "../chrome/ModalShell";
import { backgroundStatus } from "./background-format";
import {
  type BackgroundWorkSummary,
  backgroundStopRequest,
  stopQuestionMoot,
} from "./background-guard";
import { liveNow } from "./live-clock";

/** Confirms an action that would stop running work, listing it live; Enter and Esc keep the work running. */
export function BackgroundStopPrompt(): JSX.Element {
  return (
    <Show when={backgroundStopRequest()} keyed>
      {(request) => {
        const keep = () => request.settle("keep");
        const now = liveNow(() => true);
        // The live list replaces the refusal's once it has caught up; once the question is moot, the action goes
        // ahead as the user asked.
        const [caughtUp, setCaughtUp] = createSignal(false);
        createEffect(() => {
          const running = request.running();
          if (stopQuestionMoot(running, caughtUp())) request.settle("finished");
          else if (running !== null && running.length > 0) setCaughtUp(true);
        });
        const listed = (): (BackgroundWorkSummary & { endedAtMs?: number | null })[] =>
          caughtUp() ? (request.running() ?? []) : request.refused;
        return (
          <ModalShell
            labelledBy="background-stop-title"
            onDismiss={keep}
            onKeyDown={modalSubmitKeys(keep, keep)}
          >
            <div class="confirm-title" id="background-stop-title">
              Stop background work?
            </div>
            <div class="confirm-body">
              <div>
                This work is still running. Stopping it ends it, and its results won't come back.
              </div>
              <ul class="confirm-file-list confirm-background-list">
                <For each={listed()}>
                  {(item) => (
                    <li title={item.name}>
                      <span class="confirm-background-name">{item.name}</span>
                      <span>
                        {item.type} ·{" "}
                        {backgroundStatus(
                          item.state,
                          item.startedAtMs,
                          item.endedAtMs ?? null,
                          now(),
                          null,
                        )}
                      </span>
                    </li>
                  )}
                </For>
              </ul>
            </div>
            <div class="confirm-actions">
              <button type="button" class="confirm-btn" onClick={() => request.settle("stop")}>
                {request.confirmLabel}
              </button>
              <button
                type="button"
                class="confirm-btn confirm-btn-primary"
                ref={(element) => queueMicrotask(() => element.focus())}
                onClick={keep}
              >
                Keep running
              </button>
            </div>
          </ModalShell>
        );
      }}
    </Show>
  );
}
