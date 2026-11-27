import type { JSX } from "solid-js";
import { Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { keyHint } from "../commands/key-hint";
import { liveKeyHint } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentCardOpenButton } from "./AgentCardOpenButton";
import { AgentCardReplyBox } from "./AgentCardReplyBox";
import { AgentCardShell } from "./AgentCardShell";
import { NestedTranscript } from "./AgentNestedCard";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { hasPendingRequest } from "./AgentPendingRequests";
import { ownCardOnFocus, setCardCollapsed, stopAside } from "./agent-cards";
import { CollapsibleCardContent } from "./CollapsibleCardContent";
import { replyComposer } from "./composer-store";

/** A BTW's card: its transcript and reply box, with Stop while it works. A walkthrough question sets `label` and
 * `before`, the quote it was asked about. */
export function AsideEntry(props: {
  entry: AgentTranscriptEntry;
  label?: string;
  before?: JSX.Element;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  onUpdatesToggle: (entryId: string, open: boolean) => void;
  keyboardRequestKey: string | null;
  session: ClientSession;
}): JSX.Element {
  const conversationId = props.entry.conversationId;
  if (typeof conversationId !== "string" || conversationId.length === 0) {
    throw new Error("An aside entry requires a conversation id.");
  }
  const card = { kind: "aside", conversationId } as const;
  const composer = replyComposer(props.session, conversationId);
  const collapsed = () => props.entry.asideCollapsed === true;
  const toggle = () => setCardCollapsed(props.session, card, !collapsed());
  const own = ownCardOnFocus(() => ({ session: props.session, card, toggle }));

  return (
    <AgentCardShell
      label={props.label ?? props.entry.label}
      collapsed={collapsed()}
      headOnly={collapsed() && !(props.entry.asideEntries ?? []).some(hasPendingRequest)}
      meta={
        <Show when={props.entry.status !== null}>
          <small>{props.entry.status}</small>
        </Show>
      }
      wrapLabel={false}
      onToggle={toggle}
      toggleHint={liveKeyHint(CommandIds.toggleAgentAside)}
      actions={
        <>
          <Show when={props.entry.asideActive === true}>
            <button
              type="button"
              class="agent-entry-rewind"
              title={`Stop${liveKeyHint(CommandIds.stopAgentAside)}`}
              onClick={() => void stopAside(props.session, conversationId)}
            >
              Stop
            </button>
          </Show>
          <AgentCardOpenButton card={card} />
        </>
      }
      onOwn={own}
      data={{ "data-agent-aside": conversationId }}
    >
      <Show when={!collapsed()}>{props.before}</Show>
      <NestedTranscript
        entries={props.entry.asideEntries ?? []}
        pendingOnly={collapsed()}
        expandedDetails={props.expandedDetails}
        keyboardRequestKey={props.keyboardRequestKey}
        onDetailsToggle={props.onDetailsToggle}
        onUpdatesToggle={props.onUpdatesToggle}
        session={props.session}
      />
      <CollapsibleCardContent collapsed={collapsed()}>
        <Show when={props.entry.asideReplyable !== false}>
          <AgentCardReplyBox
            session={props.session}
            composer={composer}
            label="Reply to BTW"
            replyTitle="Reply"
            hint={`Paste text or images${keyHint(CommandIds.agentPaste)}`}
            disabled={props.entry.asideActive === true}
          />
        </Show>
      </CollapsibleCardContent>
    </AgentCardShell>
  );
}
