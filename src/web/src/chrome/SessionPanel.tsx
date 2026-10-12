import { Pin, PinOff } from "lucide-solid";
import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { liveKeyHint, liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { useNow } from "../compact-age";
import { floatingPanelLevel, registerFloatingPanel } from "./floating-panels";
import { panelPinned, panelWidth, setPanelPinned, setPanelWidth } from "./rail-state";
import { createSessionMenu, SessionTools } from "./SessionTools";
import { openMode, setOpenMode, trackPointer, trackPointerOut } from "./session-panel-hover";
import type { RailSession } from "./session-store";
import {
  SESSION_GROUPS,
  sessionGroup,
  sessionSubtitle,
  statusDot,
  switchShortcut,
} from "./session-summary";

const MIN_WIDTH = 200;
const MAX_WIDTH = 520;

// The session panel beside the rail: every session grouped by what needs the user, with what each agent is
// doing. Unpinned it overlays the panes (opened by hovering the rail or the panel command); pinned it docks.
export function SessionPanel(props: {
  sessions: RailSession[];
  inert: boolean;
  hasRemotes: boolean;
  remoteActive: boolean;
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

  // Pinned, the panel replaces the rail; dragging its right edge resizes it, saved when the drag ends.
  const [dragWidth, setDragWidth] = createSignal<number | null>(null);
  const startResize = (event: PointerEvent): void => {
    if (event.button !== 0) return;
    const handle = event.currentTarget as HTMLElement;
    const left = panel?.getBoundingClientRect().left ?? 0;
    handle.setPointerCapture(event.pointerId);
    const move = (next: PointerEvent): void => {
      setDragWidth(Math.round(Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, next.clientX - left))));
    };
    // Lost capture follows pointerup and pointercancel alike, so every way a drag ends saves and cleans up.
    const end = (): void => {
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("lostpointercapture", end);
      const width = dragWidth();
      setDragWidth(null);
      if (width !== null) setPanelWidth(width);
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("lostpointercapture", end);
  };

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
              ? `width:${dragWidth() ?? panelWidth()}px`
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
                remoteActive={props.remoteActive}
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
                              onContextMenu={(event) => menu.open(event, session())}
                            >
                              <span class="session-panel-num">
                                {index() < 9 ? index() + 1 : ""}
                              </span>
                              <span class={`session-status status-${statusDot(session())}`} />
                              <span class="session-panel-text">
                                <span class="session-panel-name">
                                  <span class="session-panel-title">{session().title}</span>
                                  <Show when={session().prefix}>
                                    {(prefix) => (
                                      <span class="session-panel-prefix">{prefix()}</span>
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
          <Show when={panelPinned()}>
            <div class="session-panel-resize" onPointerDown={startResize} />
          </Show>
        </nav>
      </Show>
      {/* Outside the panel, so a hover panel that closes as the pointer moves onto the menu keeps it. */}
      <menu.View />
    </>
  );
}
