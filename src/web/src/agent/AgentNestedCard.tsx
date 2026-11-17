import type { JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { hasPendingRequest } from "./AgentPendingRequests";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import { CollapsibleCardContent } from "./CollapsibleCardContent";
import { ForEachId } from "./ForEachId";

/** A nested conversation's transcript inside a BTW or subagent card. */
export function NestedTranscript(props: {
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
          <CollapsibleCardContent collapsed={props.pendingOnly && !hasPendingRequest(entry())}>
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
          </CollapsibleCardContent>
        )}
      </ForEachId>
    </div>
  );
}
