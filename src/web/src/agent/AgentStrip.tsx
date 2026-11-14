import { For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { createHorizontalOverflow, scrollWheelHorizontally } from "../chrome/horizontal-overflow";
import { registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { cardName, cardOf, openCard, openCardTitle } from "./agent-cards";
import { rovingTarget } from "./background-format";

/** A roving-focus toolbar above the composer: every expanded dashboard and BTW, which ✕ collapses. */
export function AgentStrip(props: {
  session: ClientSession;
  cards: readonly AgentTranscriptEntry[];
}): JSX.Element {
  let tray: HTMLDivElement | undefined;
  const buttons = (): HTMLButtonElement[] => [...(tray?.querySelectorAll("button") ?? [])];
  const focusAt = (index: number): void => {
    const all = buttons();
    const target = all[index];
    for (const button of all) button.tabIndex = button === target ? 0 : -1;
    target?.focus({ preventScroll: true });
    // Unlike focus's own scroll, this honors the scroll padding that keeps the pill clear of the edge fades.
    target?.scrollIntoView({ block: "nearest", inline: "nearest" });
  };
  const { overflow, track } = createHorizontalOverflow();
  const onKeyDown = (event: KeyboardEvent): void => {
    const all = buttons();
    const next = rovingTarget(
      event.key,
      all.indexOf(document.activeElement as HTMLButtonElement),
      all.length,
    );
    if (next === null) return;
    event.preventDefault();
    focusAt(next);
  };
  onMount(() =>
    onCleanup(
      registerCommand(CommandIds.showBackgroundWork, (_args, context) => {
        if (context.session !== props.session || buttons().length === 0) return false;
        focusAt(0);
        return true;
      }),
    ),
  );

  return (
    <Show when={props.cards.length > 0}>
      <div
        ref={tray}
        class="agent-status-line agent-strip"
        role="toolbar"
        aria-label="Cards"
        onKeyDown={onKeyDown}
      >
        <div
          ref={track}
          class="agent-status-scroll"
          onWheel={scrollWheelHorizontally}
          classList={{ "overflow-left": overflow().left, "overflow-right": overflow().right }}
        >
          <For each={props.cards}>
            {(entry, index) => (
              <span class="agent-strip-card" data-strip-card={entry.id}>
                <button
                  type="button"
                  class="agent-status-segment"
                  tabIndex={index() === 0 ? 0 : -1}
                  title={openCardTitle()}
                  onClick={() => void openCard(cardOf(entry)!)}
                >
                  <span class="agent-status-value agent-strip-name">{cardName(entry)}</span>
                </button>
                <button
                  type="button"
                  class="agent-status-segment agent-strip-collapse"
                  tabIndex={-1}
                  aria-label={`Collapse ${cardName(entry)}`}
                  title="Collapse or Expand Card"
                  onClick={() =>
                    void runCommandWithFeedback(CommandIds.toggleAgentAside, {
                      ...cardOf(entry),
                      collapsed: true,
                    })
                  }
                >
                  ✕
                </button>
              </span>
            )}
          </For>
        </div>
      </div>
    </Show>
  );
}
