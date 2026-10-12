import { For, type JSX, Show } from "solid-js";
import { liveKeyHint } from "../commands/keys-live";
import { dispatchCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { sessions } from "./session-store";
import { sessionSubtitle, statusDot } from "./session-summary";

// The title bar's session readout: the session on screen, then a pill for every other session waiting on the
// user, so the rail can stay a strip of dots without hiding where you are or who needs you.
export function SessionTitle(): JSX.Element {
  const current = () => sessions().find((session) => session.active);
  const waiting = () =>
    sessions().filter(
      (session) =>
        !session.active &&
        session.loaded &&
        (session.status === "needsInput" || session.status === "error"),
    );
  return (
    <div class="session-title">
      <Show when={current()}>
        {(session) => (
          <button
            type="button"
            class="session-title-current"
            title={`${session().label}${liveKeyHint(CommandIds.toggleSessionPanel)}`}
            onClick={() => void dispatchCommand(CommandIds.toggleSessionPanel)}
          >
            <span class={`session-status status-${statusDot(session())}`} />
            <span class="session-title-name">{session().title}</span>
            <Show when={session().prefix}>
              {(prefix) => <span class="session-title-prefix">{prefix()}</span>}
            </Show>
          </button>
        )}
      </Show>
      <For each={waiting()}>
        {(session) => (
          <button
            type="button"
            class="session-title-needs"
            title={`${session.label} — ${sessionSubtitle(session, Date.now())}`}
            onClick={() =>
              void dispatchCommand(CommandIds.focusSession, {
                id: session.id,
                backendId: session.backendId,
              })
            }
          >
            <span class={`session-status status-${session.status}`} />
            <span class="session-title-name">
              {session.title} {session.status === "error" ? "crashed" : "needs you"}
            </span>
          </button>
        )}
      </For>
    </div>
  );
}
