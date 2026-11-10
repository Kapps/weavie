import type { VirtualItem, Virtualizer } from "@tanstack/solid-virtual";
import { createEffect, createMemo, For, type JSX, on, onCleanup, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry, AgentTranscriptSnapshot } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";

export function AgentTranscript(props: {
  compact: boolean;
  entryAt: (virtualIndex: number) => AgentTranscriptEntry | undefined;
  entryForKey: (key: VirtualItem["key"]) => AgentTranscriptEntry | undefined;
  expandedDetails: ReadonlySet<string>;
  keyboardRequestKey: string | null;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  onLayout: () => void;
  providerName: string;
  session: ClientSession;
  transcript: AgentTranscriptSnapshot;
  virtualizer: Virtualizer<HTMLDivElement, HTMLDivElement>;
}): JSX.Element {
  // The rewind shortcut acts on the latest prompt, so only that row advertises it.
  const latestPromptTurn = createMemo(
    () => props.transcript.entries.findLast((entry) => entry.promptTurnId)?.promptTurnId ?? null,
  );
  // Rows flow oldest first; each row's top margin stands in for the unmounted rows above it.
  const rows = createMemo(() => {
    const items = props.virtualizer.getVirtualItems();
    const placed = new Map<VirtualItem["key"], { gap: number; index: number }>();
    let above = props.virtualizer.getTotalSize();
    for (let position = items.length - 1; position >= 0; position--) {
      const item = items[position]!;
      placed.set(item.key, { gap: above - item.end, index: item.index });
      above = item.start;
    }
    return { below: above, placed };
  });
  // After the whole update, once every row has rendered where the virtualizer placed it.
  createEffect(on(rows, () => queueMicrotask(props.onLayout), { defer: true }));
  return (
    <Show
      when={props.transcript.entries.length > 0}
      fallback={<EmptyState compact={props.compact} providerName={props.providerName} />}
    >
      <div
        class="agent-transcript"
        data-agent-transcript
        style={`padding-bottom:${rows().below}px`}
      >
        <For each={[...rows().placed.keys()]}>
          {(key) => {
            // By identity, so a row's view updates only when its own entry changes; a removed row keeps its
            // last entry until it unmounts.
            const entry = createMemo<AgentTranscriptEntry>(
              (last) => props.entryForKey(key) ?? last!,
            );
            const row = () => rows().placed.get(key)!;
            const previous = () => props.entryAt(row().index + 1);
            let element: HTMLDivElement | undefined;
            onCleanup(() => {
              props.virtualizer.measureElement(null);
              // A focused row scrolled out of range hands focus to the scroller, which keeps the scrolling keys.
              if (element?.contains(document.activeElement) === true) {
                element.closest<HTMLElement>(".agent-transcript-scroller")!.focus({
                  preventScroll: true,
                });
              }
            });
            return (
              <div
                class="agent-virtual-row"
                classList={{
                  "agent-virtual-row-assistant-pair":
                    entry().kind === "message" &&
                    entry().tone === "assistant" &&
                    previous()?.kind === "message" &&
                    previous()?.tone === "assistant",
                  "agent-virtual-row-first": previous() === undefined,
                  "agent-virtual-row-user": entry().kind === "message" && entry().tone === "user",
                }}
                data-index={row().index}
                data-agent-turn-output-start={
                  entry().id === props.transcript.agentTurnStartId ? "" : undefined
                }
                data-transcript-entry={entry().id}
                // Observed once its index is set; the observer measures it after layout, even mid-scroll.
                ref={(mounted) => {
                  element = mounted;
                  queueMicrotask(
                    () => mounted.isConnected && props.virtualizer.measureElement(mounted),
                  );
                }}
                style={row().gap > 0 ? `margin-top:${row().gap}px` : undefined}
              >
                <TranscriptEntry
                  expandedDetails={props.expandedDetails}
                  entry={entry()}
                  keyboardRequestKey={props.keyboardRequestKey}
                  latestPromptTurn={latestPromptTurn()}
                  onDetailsToggle={props.onDetailsToggle}
                  sectionLabel={props.transcript.sectionLabels.get(entry().id) ?? null}
                  session={props.session}
                />
              </div>
            );
          }}
        </For>
      </div>
    </Show>
  );
}

function EmptyState(props: { compact: boolean; providerName: string }): JSX.Element {
  const hints = (): { key: string; text: string }[] =>
    [
      {
        key: props.compact ? "" : liveKeyLabel(CommandIds.agentSubmit),
        text: "run the prompt — or steer a running turn",
      },
      { key: "/", text: "commands and skills" },
      { key: "↑", text: "prompt history" },
      { key: liveKeyLabel(CommandIds.agentInterrupt), text: "interrupt the turn" },
    ].filter((hint) => hint.key !== "");

  return (
    <div class="agent-empty">
      <div class="agent-empty-title ui-section-label">{props.providerName}</div>
      <p class="agent-empty-tagline">
        Describe a change, ask a question, or hand over a task — it runs in this session's worktree.
      </p>
      <dl class="agent-empty-hints">
        <For each={hints()}>
          {(hint) => (
            <>
              <dt>
                <kbd>{hint.key}</kbd>
              </dt>
              <dd>{hint.text}</dd>
            </>
          )}
        </For>
      </dl>
      <p class="agent-empty-controls">
        {props.compact ? "The header" : "The strip below the prompt"} switches the model, approvals,
        and sandbox — changes apply live.
      </p>
    </div>
  );
}
