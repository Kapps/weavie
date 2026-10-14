import { createEffect, createMemo, For, type JSX, onCleanup, Show } from "solid-js";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { useNow } from "../compact-age";
import { registerFloatingPanel } from "./floating-panels";
import { panelPinned, parkedCollapsed, setPanelPinned, setParkedCollapsed } from "./rail-state";
import { type SessionListHandlers, SessionRail } from "./SessionRail";
import {
  createSessionMenu,
  PanelControls,
  ParkedToggle,
  SessionStatus,
  SessionText,
  SessionTools,
} from "./SessionTools";
import { openMode, setOpenMode, trackPointer, trackPointerOut } from "./session-panel-hover";
import { createPanelResize } from "./session-panel-resize";
import type { RailSession } from "./session-store";
import { SESSION_GROUPS, sessionGroup, switchShortcut } from "./session-summary";

// Keyboard-navigable session entries: the pinned panel's rows, or the expanded rail's chips.
const ROWS = ".session-panel-row, .session-chip";

// The session panel: every session with what each agent is doing. Unpinned it is the rail, expanded in place by
// hovering it or the panel command; pinned it docks in the rail's place, grouped by what needs the user.
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
  const rows = (): HTMLElement[] => [...(panel?.querySelectorAll<HTMLElement>(ROWS) ?? [])];

  onCleanup(
    registerCommand(CommandIds.toggleSessionPanel, () => {
      if (openMode() === "keyboard" || (panelPinned() && panel?.contains(document.activeElement))) {
        close();
        return true;
      }
      returnFocus = document.activeElement as HTMLElement | null;
      setOpenMode("keyboard");
      queueMicrotask(() =>
        focusRow(rows().find((row) => row.classList.contains("active")) ?? rows()[0]),
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
  // While the panel is in use (hover-opened, keyboard-opened, or pinned under the pointer or focus), 1–9 switch
  // to that numbered session; a pinned panel the user isn't using leaves number keys alone.
  const onNumberKey = (event: KeyboardEvent): void => {
    if (event.ctrlKey || event.metaKey || event.altKey || !/^[1-9]$/.test(event.key)) return;
    const inUse =
      !props.inert &&
      (openMode() !== null ||
        (panelPinned() && (panel?.matches(":hover") || panel?.contains(document.activeElement))));
    const session = props.sessions[Number(event.key) - 1];
    if (!inUse || session === undefined) return;
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

  const list: SessionListHandlers = {
    ref: (element) => {
      panel = element;
    },
    onKeyDown,
    onFocusOut: (event) => {
      const next = event.relatedTarget as Node | null;
      if (openMode() === "keyboard" && !panelPinned() && !panel?.contains(next)) {
        returnFocus = null;
        setOpenMode(null);
      }
    },
  };

  return (
    <Show
      when={panelPinned()}
      fallback={
        <SessionRail
          sessions={props.sessions}
          inert={props.inert}
          hasRemotes={props.hasRemotes}
          expanded={openMode() !== null}
          now={now()}
          list={list}
          onSwitch={choose}
          onNew={props.onNew}
          onToggleRemotes={props.onToggleRemotes}
          onTogglePin={togglePin}
        />
      }
    >
      <nav
        {...list}
        class="session-panel"
        style={`width:${resize.width()}px`}
        inert={props.inert}
        aria-label="Sessions"
      >
        <div class="session-panel-head">
          <span>Sessions</span>
          <SessionTools
            hasRemotes={props.hasRemotes}
            onNew={props.onNew}
            onToggleRemotes={props.onToggleRemotes}
          />
          <PanelControls onTogglePin={togglePin} />
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
                            <span class="session-panel-num">{index() < 9 ? index() + 1 : ""}</span>
                            <SessionStatus session={session()} />
                            <SessionText session={session()} now={now()} />
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
        <div class="session-panel-resize" onPointerDown={resize.start} />
      </nav>
      <menu.View />
    </Show>
  );
}
