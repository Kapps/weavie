import { For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentAsideReply } from "./AgentAsideReply";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import { newestVisibleAgentElement } from "./AgentViewport";
import { replyComposer } from "./composer-store";

const toggles = new Map<HTMLElement, () => void>();

export function toggleAgentAside(): boolean {
  const active = document.querySelector(".agent-surface.active");
  const focused = document.activeElement?.closest<HTMLElement>(".agent-aside");
  const target =
    focused && active?.contains(focused) ? focused : newestVisibleAgentElement(".agent-aside");
  const toggle = target === undefined ? undefined : toggles.get(target);
  if (toggle === undefined) return false;
  toggle();
  return true;
}

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
  const collapsed = () => props.expandedDetails.has(props.entry.id);
  const toggle = () => props.onDetailsToggle(props.entry.id, !collapsed());
  const toggleLabel = () => (collapsed() ? "Expand BTW" : "Collapse BTW");
  const toggleTitle = () => {
    const key = liveKeyLabel(CommandIds.toggleAgentAside);
    return key === "" ? toggleLabel() : `${toggleLabel()} (${key})`;
  };
  onMount(() => {
    toggles.set(card!, toggle);
    onCleanup(() => toggles.delete(card!));
  });

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
        <span>{collapsed() ? "▸" : "▾"} BTW</span>
        <Show when={props.entry.status !== null}>
          <small>{props.entry.status}</small>
        </Show>
      </button>
      <div hidden={collapsed()}>
        <div class="agent-aside-transcript">
          <For each={props.entry.asideEntries ?? []}>
            {(entry) => (
              <TranscriptEntry
                expandedDetails={props.expandedDetails}
                entry={entry}
                keyboardRequestKey={props.keyboardRequestKey}
                onDetailsToggle={props.onDetailsToggle}
                sectionLabel={null}
                session={props.session}
              />
            )}
          </For>
        </div>
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
      </div>
    </article>
  );
}
