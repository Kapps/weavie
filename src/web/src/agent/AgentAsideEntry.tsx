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
import { ownCardOnFocus, setCardCollapsed } from "./agent-cards";
import { CollapsibleCardContent } from "./CollapsibleCardContent";
import { replyComposer } from "./composer-store";

export function AsideEntry(props: {
  entry: AgentTranscriptEntry;
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
      label={props.entry.label}
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
      actions={<AgentCardOpenButton card={card} />}
      onOwn={own}
      data={{ "data-agent-aside": conversationId }}
    >
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
            hint={`Paste text or images${keyHint(CommandIds.agentPaste)}`}
            disabled={props.entry.asideActive === true}
          />
        </Show>
      </CollapsibleCardContent>
    </AgentCardShell>
  );
}
