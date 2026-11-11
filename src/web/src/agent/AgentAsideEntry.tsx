import { type JSX, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentAsideReply } from "./AgentAsideReply";
import { NestedTranscript, registerNestedToggle } from "./AgentNestedCard";
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
  let card: HTMLElement | undefined;
  const collapsed = () => props.entry.asideCollapsed === true;
  const toggle = () =>
    props.session
      .feature("agent")
      .publish("setAsideCollapsed", { conversationId, collapsed: !collapsed() });
  const toggleLabel = () => `${collapsed() ? "Expand" : "Collapse"} ${props.entry.label}`;
  const toggleTitle = () => {
    const key = liveKeyLabel(CommandIds.toggleAgentAside);
    return key === "" ? toggleLabel() : `${toggleLabel()} (${key})`;
  };
  onMount(() => registerNestedToggle(card!, toggle));

  return (
    <article ref={card} class="agent-aside" data-agent-aside={props.entry.conversationId}>
      <button
        type="button"
        class="agent-aside-head"
        aria-label={toggleLabel()}
        aria-expanded={!collapsed()}
        title={toggleTitle()}
        onClick={toggle}
      >
        <span>
          {collapsed() ? "▸" : "▾"} {props.entry.label}
        </span>
        <Show when={props.entry.status !== null}>
          <small>{props.entry.status}</small>
        </Show>
      </button>
      <div>
        <NestedTranscript
          entries={props.entry.asideEntries ?? []}
          pendingOnly={collapsed()}
          expandedDetails={props.expandedDetails}
          keyboardRequestKey={props.keyboardRequestKey}
          onDetailsToggle={props.onDetailsToggle}
          session={props.session}
        />
        <Show when={!collapsed() && props.entry.asideReplyable !== false}>
          <Show
            when={replying()}
            fallback={
              <button
                type="button"
                class="agent-aside-reply-button ui-button ui-button-content"
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
      </div>
    </article>
  );
}
