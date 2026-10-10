import { RotateCcw } from "lucide-solid";
import { type JSX, Match, Show, Switch } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ActivityDetails, AgentRichContent } from "./AgentActivityDetails";
import { AsideEntry } from "./AgentAsideEntry";
import { ResolvedInputSummary } from "./AgentInputSummary";
import { AgentMarkdown } from "./AgentMarkdown";
import { ApprovalActions, AuthenticationActions, InputRequestActions } from "./AgentPaneActions";
import { EditLocationActions, PlanActions } from "./AgentPaneEditActions";
import { AgentLinkedText } from "./AgentPaneLinks";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentSubagentEntry } from "./AgentSubagentEntry";
import { agentControlState } from "./agent-controls-store";
import type { AgentSectionLabel } from "./pane-store";

export function TranscriptEntry(props: {
  expandedDetails: ReadonlySet<string>;
  entry: AgentTranscriptEntry;
  keyboardRequestKey: string | null;
  latestPromptTurn: string | null;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  sectionLabel: AgentSectionLabel | null;
  session: ClientSession;
}): JSX.Element {
  if (props.entry.kind === "subagent") {
    return (
      <AgentSubagentEntry
        entry={props.entry}
        expandedDetails={props.expandedDetails}
        onDetailsToggle={props.onDetailsToggle}
        keyboardRequestKey={props.keyboardRequestKey}
        session={props.session}
      />
    );
  }
  if (props.entry.kind === "aside") {
    return (
      <AsideEntry
        entry={props.entry}
        expandedDetails={props.expandedDetails}
        onDetailsToggle={props.onDetailsToggle}
        keyboardRequestKey={props.keyboardRequestKey}
        session={props.session}
      />
    );
  }
  return (
    <article
      class={`agent-entry agent-entry-${props.entry.kind} agent-tone-${props.entry.tone}`}
      classList={{
        "agent-entry-edit": props.entry.actionMessage?.type === "edit-location",
        "agent-entry-result": props.sectionLabel !== null,
      }}
    >
      <Show when={props.sectionLabel !== null || showEntryHeader(props.entry)}>
        <div class="agent-entry-head" title={entryTitle(props.entry)}>
          <span class="agent-entry-label">
            {props.entry.kind === "plan" && props.sectionLabel !== null
              ? `${props.sectionLabel} · Plan`
              : (props.sectionLabel ?? entryLabel(props.entry))}
          </span>
          <Show when={props.entry.status !== null}>
            <small class="agent-entry-status">{props.entry.status}</small>
          </Show>
          <Show when={agentControlState(props.session).rewindable && props.entry.promptTurnId}>
            {(turnId) => (
              <RewindAction latest={turnId() === props.latestPromptTurn} turnId={turnId()} />
            )}
          </Show>
        </div>
      </Show>
      <div class="agent-entry-main">
        <Show when={props.entry.summary !== null}>
          <div class="agent-entry-summary">
            <AgentLinkedText session={props.session} text={props.entry.summary ?? ""} />
          </div>
        </Show>
        <Show when={props.entry.text !== null}>
          <Show
            when={props.entry.kind === "message" && props.entry.tone === "assistant"}
            fallback={
              <pre class="agent-entry-text">
                <AgentLinkedText session={props.session} text={props.entry.text ?? ""} />
              </pre>
            }
          >
            <AgentMarkdown
              cacheKey={props.entry}
              content={props.entry.text ?? ""}
              renderMermaid={!props.entry.streaming}
              session={props.session}
            />
          </Show>
        </Show>
        <AgentMedia message={props.entry.actionMessage} session={props.session} />
        <AgentRichContent message={props.entry.actionMessage} session={props.session} />
        <Show when={props.entry.detailCount > 0}>
          <ActivityDetails
            entry={props.entry}
            expanded={props.expandedDetails.has(props.entry.id)}
            onToggle={(open) => props.onDetailsToggle(props.entry.id, open)}
            session={props.session}
            steps={props.entry.details}
          />
        </Show>
        <EntryActions
          detailsExpanded={props.expandedDetails.has(props.entry.id)}
          entry={props.entry}
          keyboardRequestKey={props.keyboardRequestKey}
          onDetailsToggle={(open) => props.onDetailsToggle(props.entry.id, open)}
          session={props.session}
        />
      </div>
    </article>
  );
}

function RewindAction(props: { latest: boolean; turnId: string }): JSX.Element {
  const key = (): string => (props.latest ? liveKeyLabel(CommandIds.rewindAgentConversation) : "");
  return (
    <button
      type="button"
      class="agent-entry-rewind"
      title={key() === "" ? "Rewind to here" : `Rewind to here (${key()})`}
      onClick={() =>
        void runCommandWithFeedback(CommandIds.rewindAgentConversation, { turnId: props.turnId })
      }
    >
      <RotateCcw size={12} aria-hidden="true" />
      Rewind
    </button>
  );
}

function showEntryHeader(entry: AgentTranscriptEntry): boolean {
  return entry.kind !== "message" || entry.tone !== "assistant";
}

function EntryActions(props: {
  detailsExpanded: boolean;
  entry: AgentTranscriptEntry;
  keyboardRequestKey: string | null;
  onDetailsToggle: (open: boolean) => void;
  session: ClientSession;
}): JSX.Element {
  return (
    <Show when={props.entry.actionMessage}>
      {(message) => (
        <Switch>
          <Match when={message().type === "approval-requested" && props.entry.status === "pending"}>
            <ApprovalActions
              session={props.session}
              message={message()}
              answersToKeys={props.entry.id === props.keyboardRequestKey}
            />
          </Match>
          <Match
            when={message().type === "authentication-requested" && props.entry.status === "pending"}
          >
            <AuthenticationActions
              session={props.session}
              message={message()}
              answersToKeys={props.entry.id === props.keyboardRequestKey}
            />
          </Match>
          <Match when={message().type === "input-requested" && props.entry.status === "pending"}>
            <InputRequestActions
              session={props.session}
              message={message()}
              answersToKeys={props.entry.id === props.keyboardRequestKey}
            />
          </Match>
          <Match
            when={message().type === "input-requested" && (message().questions?.length ?? 0) > 0}
          >
            <ResolvedInputSummary
              expanded={props.detailsExpanded}
              message={message()}
              onToggle={props.onDetailsToggle}
            />
          </Match>
          <Match when={message().type === "edit-location"}>
            <EditLocationActions session={props.session} message={message()} />
          </Match>
          <Match
            when={(message().locations?.length ?? 0) > 0 || (message().diffs?.length ?? 0) > 0}
          >
            <EditLocationActions session={props.session} message={message()} />
          </Match>
          <Match when={message().type === "item-completed" && message().itemType === "plan"}>
            <PlanActions message={message()} session={props.session} />
          </Match>
        </Switch>
      )}
    </Show>
  );
}

function AgentMedia(props: {
  message: import("../bridge").AgentPaneUpdate | null;
  session: ClientSession;
}): JSX.Element {
  const source = (): string | null => {
    const message = props.message;
    return message?.mediaData !== null && message?.mediaData !== undefined && message.mediaType
      ? `data:${message.mediaType};base64,${message.mediaData}`
      : null;
  };
  return (
    <>
      <Show when={source() !== null && props.message?.mediaType?.startsWith("image/")}>
        <img
          class="agent-entry-media"
          src={source() ?? ""}
          alt={props.message?.type === "user-image" ? "Attached image" : "Agent-provided content"}
        />
      </Show>
      <Show when={source() !== null && props.message?.mediaType?.startsWith("audio/")}>
        <a class="agent-entry-media" href={source() ?? ""} download="agent-provided-audio">
          Download agent-provided audio
        </a>
      </Show>
      <Show when={props.message?.resourceUri}>
        {(uri) => (
          <pre class="agent-entry-resource">
            {props.message?.type === "input-requested" && props.message.itemType === "url" ? (
              uri()
            ) : (
              <AgentLinkedText session={props.session} text={uri()} />
            )}
          </pre>
        )}
      </Show>
    </>
  );
}

function entryLabel(entry: AgentTranscriptEntry): string {
  if (entry.kind === "message" && entry.tone === "user") {
    return entry.label === "Steer" || entry.label === "Command" ? entry.label : "Prompt";
  }
  switch (entry.label) {
    case "Interrupted":
      return "Interrupted";
    case "Permission":
      return "Permission";
    case "Warning":
      return "Warning";
    case "Working":
      return "Working";
    default:
      return entry.label;
  }
}

function entryTitle(entry: AgentTranscriptEntry): string {
  return entry.status === null ? entry.label : `${entry.label} ${entry.status}`;
}
