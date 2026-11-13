import type { JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentCurrentThought } from "./AgentCurrentThought";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { isPendingRequest } from "./AgentPendingRequests";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import { ForEachId } from "./ForEachId";

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
  return (
    <div class="agent-aside-transcript">
      <ForEachId each={props.entries}>
        {(entry) => (
          <div
            hidden={
              props.pendingOnly &&
              !isPendingRequest(entry()) &&
              (entry().pendingEntries?.length ?? 0) === 0
            }
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
        )}
      </ForEachId>
      <AgentCurrentThought text={props.pendingOnly ? null : props.currentThought} />
    </div>
  );
}
