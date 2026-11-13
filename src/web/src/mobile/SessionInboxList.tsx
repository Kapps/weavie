import { createMemo, createSignal, For, type JSX, Show } from "solid-js";
import { ContextMenu, type ContextMenuState } from "../chrome/ContextMenu";
import { sessionMenuAt } from "../chrome/session-menu";
import type { RailSession } from "../chrome/session-store";
import { SESSION_GROUPS, sessionGroup } from "../chrome/session-summary";
import { captureCommandRunner } from "../commands/registry";
import { holdToOpen } from "./long-press";
import { SessionInboxRow } from "./SessionInboxRow";

/** Touch chrome's session list, grouped by attention with session management on each row. */
export function SessionInboxList(props: {
  sessions: RailSession[];
  onOpen: (session: RailSession) => Promise<boolean>;
}): JSX.Element {
  const [sessionMenu, setSessionMenu] = createSignal<ContextMenuState | null>(null);
  const openSessionMenu = (session: RailSession, x: number, y: number): void => {
    setSessionMenu(sessionMenuAt(session, x, y, captureCommandRunner()));
  };
  // The list owns the gesture so a catalog update rebuilding a row cannot drop a hold in progress.
  const holdRow = holdToOpen((x, y, pressed) => {
    const row = pressed instanceof Element ? pressed.closest("[data-session-id]") : null;
    if (row === null) return false;
    const session = props.sessions.find(
      (candidate) =>
        candidate.id === row.getAttribute("data-session-id") &&
        candidate.backendId === row.getAttribute("data-backend-id"),
    );
    if (session === undefined) return false;
    openSessionMenu(session, x, y);
    return true;
  });

  return (
    <>
      <section class="session-inbox-list" aria-label="Available sessions" {...holdRow}>
        <Show
          when={props.sessions.length > 0}
          fallback={<p class="session-inbox-empty">No sessions yet.</p>}
        >
          <For each={SESSION_GROUPS}>
            {(group) => {
              const members = createMemo(() =>
                props.sessions.filter((session) => sessionGroup(session) === group.id),
              );
              return (
                <Show when={members().length > 0}>
                  <section class="session-inbox-group" aria-label={group.title}>
                    <h2 class="session-inbox-group-title ui-section-label">{group.title}</h2>
                    <For each={members()}>
                      {(session) => (
                        <SessionInboxRow
                          session={session}
                          onOpen={props.onOpen}
                          onManage={openSessionMenu}
                        />
                      )}
                    </For>
                  </section>
                </Show>
              );
            }}
          </For>
        </Show>
      </section>
      <Show when={sessionMenu()}>
        {(menu) => <ContextMenu menu={menu()} onClose={() => setSessionMenu(null)} />}
      </Show>
    </>
  );
}
