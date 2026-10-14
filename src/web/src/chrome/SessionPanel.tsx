import { Pin, PinOff } from "lucide-solid";
import { createEffect, createMemo, For, type JSX, onCleanup, Show } from "solid-js";
import { liveKeyHint, liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { useNow } from "../compact-age";
import { floatingPanelLevel, registerFloatingPanel } from "./floating-panels";
import { panelPinned, parkedCollapsed, setPanelPinned, setParkedCollapsed } from "./rail-state";
import { createSessionMenu, ParkedToggle, SessionStatus, SessionTools } from "./SessionTools";
import { openMode, setOpenMode, trackPointer, trackPointerOut } from "./session-panel-hover";
import { createPanelResize } from "./session-panel-resize";
import type { RailSession } from "./session-store";
import { SESSION_GROUPS, sessionGroup, sessionSubtitle, switchShortcut } from "./session-summary";

// The session panel beside the rail: every session grouped by what needs the user, with what each agent is
// doing. Unpinned it overlays the panes (opened by hovering the rail or the panel command); pinned it docks.
export function SessionPanel(props: {
  sessions: RailSession[];
  inert: boolean;
  hasRemotes: boolean;
  onSwitch: (session: RailSession) => void;
  onNew: () => void;
  onToggleRemotes: (anchor: DOMRect) => void;
}): JSX.Element {
  const now = useNow();
  window.addEventListener("pointermove", trackPointer, { passive: true });
  window.addEventListener("pointerout", trackPointerOut, { passive: true });
  onCleanup(() => {
    window.removeEventListener("pointermove", trackPointer);
    window.removeEventListener("pointerout", trackPointerOut);
  });
  // Rows key on session identity, not object identity: chips are rebuilt on every status or activity push, and
  // re-creating a row would drop keyboard focus mid-navigation.
  const byKey = createMemo(
    () =>
      new Map(
        props.sessions.map((session, index) => [
          `${session.backendId} ${session.id}`,
          { session, index },
        ]),
      ),
  );
  const visible = (): boolean => panelPinned() || openMode() !== null;
  let panel: HTMLElement | undefined;
  let returnFocus: HTMLElement | null = null;

  const close = (): void => {
    setOpenMode(null);
    returnFocus?.focus();
    returnFocus = null;
  };
  // focusVisible: a keyboard-opened panel shows the focus ring on its first row, not only after an arrow key.
  const focusRow = (row: Element | undefined | null): void =>
    (row as HTMLElement | null)?.focus({ focusVisible: true } as FocusOptions);
  const rows = (): HTMLElement[] => [
    ...(panel?.querySelectorAll<HTMLElement>(".session-panel-row") ?? []),
  ];

  onCleanup(
    registerCommand(CommandIds.toggleSessionPanel, () => {
      if (openMode() === "keyboard" || (panelPinned() && panel?.contains(document.activeElement))) {
        close();
        return true;
      }
      returnFocus = document.activeElement as HTMLElement | null;
      setOpenMode("keyboard");
      queueMicrotask(() =>
        focusRow(panel?.querySelector(".session-panel-row.active") ?? rows()[0]),
      );
      return true;
    }),
  );
  // Unpinned under the pointer, it stays as a hover panel until the pointer leaves.
  const togglePin = (): void => {
    const pinned = !panelPinned();
    if (!pinned && panel?.matches(":hover")) setOpenMode("hover");
    setPanelPinned(pinned);
  };
  onCleanup(
    registerCommand(CommandIds.toggleParkedSessions, () => {
      setParkedCollapsed(!parkedCollapsed());
      return true;
    }),
  );
  onCleanup(
    registerCommand(CommandIds.pinSessionPanel, () => {
      togglePin();
      return true;
    }),
  );
  // Unpinned, the panel is a popover: Escape and opening a tool window dismiss it.
  createEffect(() => {
    if (openMode() !== null && !panelPinned()) {
      onCleanup(registerFloatingPanel("session-panel", close, "popover").dispose);
    }
  });

  const onKeyDown = (event: KeyboardEvent): void => {
    // Unpinned, Escape belongs to the floating-panel dismissal; pinned, it hands focus back.
    if (event.key === "Escape" && panelPinned()) {
      event.preventDefault();
      close();
      return;
    }
    const list = rows();
    const at = list.indexOf(document.activeElement as HTMLElement);
    const step: Record<string, number | undefined> = { ArrowDown: at + 1, ArrowUp: at - 1 };
    const target =
      event.key === "Home" ? 0 : event.key === "End" ? list.length - 1 : step[event.key];
    if (target === undefined) return;
    event.preventDefault();
    focusRow(list[Math.max(0, Math.min(list.length - 1, target))]);
  };
  // While the unpinned panel is open, 1–9 switch to that numbered session; a pinned panel leaves number keys alone.
  const onNumberKey = (event: KeyboardEvent): void => {
    if (event.ctrlKey || event.metaKey || event.altKey || !/^[1-9]$/.test(event.key)) return;
    const session = props.sessions[Number(event.key) - 1];
    if (props.inert || openMode() === null || panelPinned() || session === undefined) return;
    event.preventDefault();
    event.stopPropagation();
    choose(session);
  };
  window.addEventListener("keydown", onNumberKey, { capture: true });
  onCleanup(() => window.removeEventListener("keydown", onNumberKey, { capture: true }));

  // Focus goes back where it was before the switch, so the switch carries it into the same pane of the new session.
  const choose = (session: RailSession): void => {
    const target = returnFocus;
    returnFocus = null;
    target?.focus();
    if (!panelPinned()) setOpenMode(null);
    props.onSwitch(session);
  };

  const resize = createPanelResize(() => panel);

  const menu = createSessionMenu();

  return (
    <>
      <Show when={visible()}>
        <nav
          class="session-panel"
          classList={{ pinned: panelPinned() }}
          // Unpinned it's a popover, layered with the floating tool windows it overlays.
          style={
            panelPinned()
              ? `width:${resize.width()}px`
              : `z-index:${floatingPanelLevel("session-panel")}`
          }
          ref={panel}
          inert={props.inert}
          aria-label="Sessions"
          onKeyDown={onKeyDown}
          onFocusOut={(event) => {
            const next = event.relatedTarget as Node | null;
            if (openMode() === "keyboard" && !panelPinned() && !panel?.contains(next)) {
              returnFocus = null;
              setOpenMode(null);
            }
          }}
        >
          <div class="session-panel-head">
            <span>Sessions</span>
            <Show when={liveKeyLabel(CommandIds.toggleSessionPanel)}>
              {(keys) => <span class="session-panel-key">{keys()}</span>}
            </Show>
            <Show when={panelPinned()}>
              <SessionTools
                hasRemotes={props.hasRemotes}
                onNew={props.onNew}
                onToggleRemotes={props.onToggleRemotes}
              />
            </Show>
            <button
              type="button"
              class="session-panel-pin"
              classList={{ on: panelPinned() }}
              title={`${panelPinned() ? "Unpin" : "Pin"} session panel${liveKeyHint(CommandIds.pinSessionPanel)}`}
              aria-pressed={panelPinned()}
              onClick={togglePin}
            >
              <Show when={panelPinned()} fallback={<Pin size={13} />}>
                <PinOff size={13} />
              </Show>
            </button>
          </div>
          <For each={SESSION_GROUPS}>
            {(group) => {
              const members = createMemo(
                () =>
                  [...byKey()]
                    .filter(([, entry]) => sessionGroup(entry.session) === group.id)
                    .map(([key]) => key),
                [],
                { equals: (a, b) => a.length === b.length && a.every((key, i) => key === b[i]) },
              );
              return (
                <Show when={members().length > 0}>
                  <Show
                    when={group.id === "parked"}
                    fallback={<div class="session-panel-group">{group.title}</div>}
                  >
                    <ParkedToggle
                      class="session-panel-group session-panel-parked"
                      count={members().length}
                      label={`${group.title} · ${members().length}`}
                    />
                  </Show>
                  <For each={group.id === "parked" && parkedCollapsed() ? [] : members()}>
                    {(key) => (
                      <Show when={byKey().get(key)}>
                        {(entry) => {
                          const session = (): RailSession => entry().session;
                          const index = (): number => entry().index;
                          const shortcut = (): string => switchShortcut(index());
                          return (
                            <button
                              type="button"
                              class="session-panel-row"
                              classList={{
                                active: session().active,
                                parked: !session().loaded,
                                offline: session().offline,
                              }}
                              data-session-slot={session().id}
                              title={`${session().label}${session().isLocal ? "" : ` @ ${session().locationName}`}${
                                shortcut() === "" ? "" : ` (${shortcut()})`
                              }`}
                              onClick={() => choose(session())}
                              onContextMenu={(event) => menu.open(event, session())}
                            >
                              <span class="session-panel-num">
                                {index() < 9 ? index() + 1 : ""}
                              </span>
                              <SessionStatus session={session()} />
                              <span class="session-panel-text">
                                <span class="session-panel-title">{session().title}</span>
                                <span class="session-panel-activity">
                                  {sessionSubtitle(session(), now())}
                                </span>
                              </span>
                            </button>
                          );
                        }}
                      </Show>
                    )}
                  </For>
                </Show>
              );
            }}
          </For>
          <Show when={panelPinned()}>
            <div class="session-panel-resize" onPointerDown={resize.start} />
          </Show>
        </nav>
      </Show>
      {/* Outside the panel, so a hover panel that closes as the pointer moves onto the menu keeps it. */}
      <menu.View />
    </>
  );
}
