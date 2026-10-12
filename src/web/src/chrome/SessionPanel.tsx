import { Pin, PinOff } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { liveKeyHint, liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { useNow } from "../compact-age";
import { floatingPanelLevel, registerFloatingPanel } from "./floating-panels";
import { panelPinned, setPanelPinned } from "./rail-state";
import type { RailSession } from "./session-store";
import {
  SESSION_GROUPS,
  sessionGroup,
  sessionSubtitle,
  statusDot,
  switchShortcut,
} from "./session-summary";

// How the unpinned panel was opened: hovering the rail closes it on leave; the keyboard closes it on Escape.
type OpenMode = "hover" | "keyboard";
const [openMode, setOpenMode] = createSignal<OpenMode | null>(null);
const HOVER_OPEN_MS = 150;
const HOVER_CLOSE_MS = 250;
let opening: ReturnType<typeof setTimeout> | undefined;
let closing: ReturnType<typeof setTimeout> | undefined;
// A rail click is a direct choice, so hover stays inert until the pointer is seen outside the rail.
let clicked = false;

// Hover follows where the pointer is seen, not enter/leave events: switching sessions rebuilds the dot under the
// pointer, after which Chrome can report a re-entry with no move and drop the later leave.
function trackPointer(event: PointerEvent): void {
  locatePointer(event.target instanceof Element ? event.target : null);
}

// Leaving the window or entering an iframe sends no further moves to this document; the pointerout says where it went.
function trackPointerOut(event: PointerEvent): void {
  const next = event.relatedTarget instanceof Element ? event.relatedTarget : null;
  if (next === null || next instanceof HTMLIFrameElement) locatePointer(null);
}

function locatePointer(target: Element | null): void {
  if (target?.closest(".session-rail, .session-panel") == null) {
    clicked = false;
    clearTimeout(opening);
    opening = undefined;
    if (openMode() === "hover" && closing === undefined) {
      closing = setTimeout(() => {
        closing = undefined;
        if (openMode() === "hover") setOpenMode(null);
      }, HOVER_CLOSE_MS);
    }
    return;
  }
  clearTimeout(closing);
  closing = undefined;
  if (target.closest(".session-rail") === null || clicked) return;
  if (openMode() === null && opening === undefined) {
    opening = setTimeout(() => {
      opening = undefined;
      if (openMode() === null) setOpenMode("hover");
    }, HOVER_OPEN_MS);
  }
}

/** A click on the rail cancels a pending or hover-opened panel until the pointer leaves the rail. */
export function cancelSessionPanelHover(): void {
  clicked = true;
  clearTimeout(opening);
  opening = undefined;
  if (openMode() === "hover") setOpenMode(null);
}

// The session panel beside the rail: every session grouped by what needs the user, with what each agent is
// doing. Unpinned it overlays the panes (opened by hovering the rail or the panel command); pinned it docks.
export function SessionPanel(props: {
  sessions: RailSession[];
  inert: boolean;
  onSwitch: (session: RailSession) => void;
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
  // Focus goes back where it was before the switch, so the switch carries it into the same pane of the new session.
  const choose = (session: RailSession): void => {
    const target = returnFocus;
    returnFocus = null;
    target?.focus();
    if (!panelPinned()) setOpenMode(null);
    props.onSwitch(session);
  };

  return (
    <Show when={visible()}>
      <nav
        class="session-panel"
        classList={{ pinned: panelPinned() }}
        // Unpinned it's a popover, layered with the floating tool windows it overlays.
        style={panelPinned() ? "" : `z-index:${floatingPanelLevel("session-panel")}`}
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
                <div class="session-panel-group">{group.title}</div>
                <For each={members()}>
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
                            title={
                              shortcut() === ""
                                ? session().label
                                : `${session().label} (${shortcut()})`
                            }
                            onClick={() => choose(session())}
                          >
                            <span class="session-panel-num">{index() < 9 ? index() + 1 : ""}</span>
                            <span class={`session-status status-${statusDot(session())}`} />
                            <span class="session-panel-text">
                              <span class="session-panel-name">
                                <span class="session-panel-title">{session().title}</span>
                                <Show when={session().prefix}>
                                  {(prefix) => <span class="session-panel-prefix">{prefix()}</span>}
                                </Show>
                              </span>
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
      </nav>
    </Show>
  );
}
