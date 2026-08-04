import type { Virtualizer } from "@tanstack/solid-virtual";
import { For, type JSX, onCleanup, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import type { AgentSectionLabel } from "./pane-store";

export function AgentTranscript(props: {
  agentTurnStartId: string | null;
  compact: boolean;
  entries: AgentTranscriptEntry[];
  expandedDetails: ReadonlySet<string>;
  keyboardApprovalId: string | null;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  providerName: string;
  sectionLabels: ReadonlyMap<string, AgentSectionLabel>;
  session: ClientSession;
  virtualizer: Virtualizer<HTMLDivElement, HTMLDivElement>;
}): JSX.Element {
  return (
    <Show
      when={props.entries.length > 0}
      fallback={<EmptyState compact={props.compact} providerName={props.providerName} />}
    >
      <div
        class="agent-transcript"
        data-agent-transcript
        style={`height:${props.virtualizer.getTotalSize()}px`}
      >
        <For each={props.virtualizer.getVirtualItems()}>
          {(virtualRow) => {
            const entry = (): AgentTranscriptEntry => props.entries[virtualRow.index]!;
            const previous = (): AgentTranscriptEntry | undefined =>
              props.entries[virtualRow.index - 1];
            onCleanup(() => props.virtualizer.measureElement(null));
            return (
              <div
                class="agent-virtual-row"
                classList={{
                  "agent-virtual-row-assistant-pair":
                    entry().kind === "message" &&
                    entry().tone === "assistant" &&
                    previous()?.kind === "message" &&
                    previous()?.tone === "assistant",
                  "agent-virtual-row-first": virtualRow.index === 0,
                  "agent-virtual-row-user": entry().kind === "message" && entry().tone === "user",
                }}
                data-index={virtualRow.index}
                data-agent-turn-output-start={
                  entry().id === props.agentTurnStartId ? "" : undefined
                }
                data-transcript-entry={entry().id}
                ref={(element) =>
                  queueMicrotask(() => {
                    if (element.isConnected) {
                      props.virtualizer.measureElement(element);
                    }
                  })
                }
                style={`transform:translateY(${virtualRow.start}px)`}
              >
                <TranscriptEntry
                  detailsExpanded={props.expandedDetails.has(entry().id)}
                  entry={entry()}
                  keyboardApprovalId={props.keyboardApprovalId}
                  onDetailsToggle={(open) => props.onDetailsToggle(entry().id, open)}
                  sectionLabel={props.sectionLabels.get(entry().id) ?? null}
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
      <div class="agent-empty-title">{props.providerName}</div>
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
