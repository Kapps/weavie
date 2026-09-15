import { For, type JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import type { AgentSectionLabel } from "./pane-store";

export function AgentTranscriptRow(props: {
  agentTurnStartId: string | null;
  entry: AgentTranscriptEntry;
  index: number;
  previous: AgentTranscriptEntry | undefined;
  expandedDetails: ReadonlySet<string>;
  keyboardRequestKey: string | null;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  sectionLabels: ReadonlyMap<string, AgentSectionLabel>;
  session: ClientSession;
}): JSX.Element {
  return (
    <div
      class="agent-virtual-row"
      classList={{
        "agent-virtual-row-assistant-pair":
          props.entry.kind === "message" &&
          props.entry.tone === "assistant" &&
          props.previous?.kind === "message" &&
          props.previous?.tone === "assistant",
        "agent-virtual-row-first": props.index === 0,
        "agent-virtual-row-user": props.entry.kind === "message" && props.entry.tone === "user",
      }}
      data-index={props.index}
      data-agent-turn-output-start={props.entry.id === props.agentTurnStartId ? "" : undefined}
      data-transcript-entry={props.entry.id}
    >
      <TranscriptEntry
        expandedDetails={props.expandedDetails}
        entry={props.entry}
        keyboardRequestKey={props.keyboardRequestKey}
        onDetailsToggle={props.onDetailsToggle}
        sectionLabel={props.sectionLabels.get(props.entry.id) ?? null}
        session={props.session}
      />
    </div>
  );
}

export function AgentEmptyState(props: { compact: boolean; providerName: string }): JSX.Element {
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
