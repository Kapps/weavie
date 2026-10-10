import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentAsideReply } from "./AgentAsideReply";
import { AgentNestedCard } from "./AgentNestedCard";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { replyComposer } from "./composer-store";

export function AsideEntry(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  keyboardRequestKey: string | null;
  session: ClientSession;
}): JSX.Element {
  const conversationId = props.entry.conversationId;
  if (typeof conversationId !== "string" || conversationId.length === 0) {
    throw new Error("An aside entry requires a conversation id.");
  }
  const composer = replyComposer(props.session, conversationId);
  const replying = () => composer.state().replyOpen;
  const collapsed = () => props.expandedDetails.has(props.entry.id);

  return (
    <AgentNestedCard
      attributes={{ "data-agent-aside": conversationId }}
      collapsed={collapsed()}
      entries={props.entry.asideEntries ?? []}
      expandedDetails={props.expandedDetails}
      head={
        <>
          <span>BTW</span>
          <Show when={props.entry.status !== null}>
            <small>{props.entry.status}</small>
          </Show>
        </>
      }
      keyboardRequestKey={props.keyboardRequestKey}
      label="BTW"
      onDetailsToggle={props.onDetailsToggle}
      onToggle={() => props.onDetailsToggle(props.entry.id, !collapsed())}
      session={props.session}
    >
      <Show when={props.entry.asideReplyable !== false}>
        <Show
          when={replying()}
          fallback={
            <button
              type="button"
              class="agent-aside-reply-button"
              disabled={props.entry.asideActive === true}
              onClick={() => composer.setOpen(true)}
            >
              Reply
            </button>
          }
        >
          <AgentAsideReply
            session={props.session}
            conversationId={conversationId}
            onClose={() => composer.setOpen(false)}
          />
        </Show>
      </Show>
    </AgentNestedCard>
  );
}
