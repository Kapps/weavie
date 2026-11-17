import { createMemo, type JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentCurrentThought } from "./AgentCurrentThought";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { hasPendingRequest } from "./AgentPendingRequests";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import { CollapsibleCardContent } from "./CollapsibleCardContent";
import { ForEachId } from "./ForEachId";
import { transcriptRowClasses } from "./transcript-row";

/** A nested conversation's transcript inside a BTW or subagent card. */
export function NestedTranscript(props: {
  currentThought: string | null;
  entries: readonly AgentTranscriptEntry[];
  pendingOnly: boolean;
  expandedDetails: ReadonlySet<string>;
  keyboardRequestKey: string | null;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  onUpdatesToggle: (entryId: string, open: boolean) => void;
  session: ClientSession;
}): JSX.Element {
  const previous = createMemo(
    () => new Map(props.entries.map((entry, index) => [entry.id, props.entries[index - 1]])),
  );
  return (
    <div class="agent-aside-transcript">
      <ForEachId each={props.entries}>
        {(entry) => (
          <CollapsibleCardContent collapsed={props.pendingOnly && !hasPendingRequest(entry())}>
            <div
              class="agent-row"
              classList={transcriptRowClasses(entry(), previous().get(entry().id))}
            >
              <TranscriptEntry
                expandedDetails={props.expandedDetails}
                entry={entry()}
                keyboardRequestKey={props.keyboardRequestKey}
                latestPromptTurn={null}
                onDetailsToggle={props.onDetailsToggle}
                onUpdatesToggle={props.onUpdatesToggle}
                sectionLabel={null}
                session={props.session}
              />
            </div>
          </CollapsibleCardContent>
        )}
      </ForEachId>
      <CollapsibleCardContent collapsed={props.pendingOnly}>
        <AgentCurrentThought text={props.currentThought} />
      </CollapsibleCardContent>
    </div>
  );
}
