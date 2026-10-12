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
let hoverTimer: ReturnType<typeof setTimeout> | undefined;
// Switching sessions rebuilds the dot under a resting pointer, which re-fires the rail's pointerenter without the
// pointer ever leaving; only a real entry counts.
let inRail = false;

/** The pointer entered or left the rail: resting on it opens the panel, leaving rail and panel closes it. */
export function hoverRail(inside: boolean): void {
  if (inside !== inRail) {
    inRail = inside;
    hoverSessionPanel(inside);
  }
}

function hoverSessionPanel(inside: boolean): void {
  clearTimeout(hoverTimer);
  if (inside && openMode() === null) {
    hoverTimer = setTimeout(() => setOpenMode("hover"), HOVER_OPEN_MS);
  } else if (!inside && openMode() === "hover") {
    hoverTimer = setTimeout(() => setOpenMode(null), HOVER_CLOSE_MS);
  }
}

/** A click on the rail is a direct choice, so it cancels a pending or hover-opened panel. */
export function cancelSessionPanelHover(): void {
  clearTimeout(hoverTimer);
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
  const focusRow = (row: Element | undefined | null): void => (row as HTMLElement | null)?.focus();
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
  onCleanup(
    registerCommand(CommandIds.pinSessionPanel, () => {
      setPanelPinned(!panelPinned());
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
    const list = rows();
    const at = list.indexOf(document.activeElement as HTMLElement);
    const step: Record<string, number | undefined> = { ArrowDown: at + 1, ArrowUp: at - 1 };
    const target =
      event.key === "Home" ? 0 : event.key === "End" ? list.length - 1 : step[event.key];
    if (target === undefined) return;
    event.preventDefault();
    focusRow(list[Math.max(0, Math.min(list.length - 1, target))]);
  };
  const choose = (session: RailSession): void => {
    if (!panelPinned()) setOpenMode(null);
    returnFocus = null;
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
        onPointerEnter={() => hoverSessionPanel(true)}
        onPointerLeave={() => hoverSessionPanel(false)}
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
            onClick={() => setPanelPinned(!panelPinned())}
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
                                {session().title}
                                <Show when={session().prefix}>
                                  {(prefix) => (
                                    <span class="session-panel-prefix"> {prefix()}</span>
                                  )}
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
