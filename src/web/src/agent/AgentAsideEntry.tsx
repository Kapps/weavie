import type { JSX } from "solid-js";
import { Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { AgentAsideReplyBox } from "./AgentAsideReplyBox";
import { AgentCardShell } from "./AgentCardShell";
import { CollapsibleCardContent, NestedTranscript } from "./AgentNestedCard";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { ownCardOnFocus, setCardCollapsed } from "./agent-cards";

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
  const collapsed = () => props.entry.asideCollapsed === true;
  const toggle = () => setCardCollapsed(props.session, card, !collapsed());
  const own = ownCardOnFocus(() => ({ session: props.session, card, toggle }));

  return (
    <AgentCardShell
      label={props.entry.label}
      collapsed={collapsed()}
      meta={
        <Show when={props.entry.status !== null}>
          <small>{props.entry.status}</small>
        </Show>
      }
      onToggle={toggle}
      card={card}
      onOwn={own}
      data={{ "data-agent-aside": conversationId }}
    >
      <div>
        <NestedTranscript
          currentThought={props.entry.currentThought ?? null}
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
            <AgentAsideReplyBox
              session={props.session}
              conversationId={conversationId}
              disabled={props.entry.asideActive === true}
            />
          </Show>
        </CollapsibleCardContent>
      </div>
    </AgentCardShell>
  );
}
