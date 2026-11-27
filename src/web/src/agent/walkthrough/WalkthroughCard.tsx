import { For, type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { liveKeyHint } from "../../commands/keys-live";
import { CommandIds } from "../../commands/types";
import { cardTabPath } from "../../editor/tab-entry";
import { AgentCardOpenButton } from "../AgentCardOpenButton";
import { AgentCardShell } from "../AgentCardShell";
import { AgentMarkdown } from "../AgentMarkdown";
import type { AgentBlockTranscriptEntry } from "../AgentPaneTranscriptTypes";
import { hasPendingRequest } from "../AgentPendingRequests";
import { openCard, ownCardOnFocus, setCardCollapsed } from "../agent-cards";
import { agentBlock } from "../block-store";
import { StopRow } from "./WalkthroughStop";
import { walkthroughQuestions } from "./walkthrough-page";
import { requestTarget, type WalkthroughTarget } from "./walkthrough-view";
import "./walkthrough.css";

/** A walkthrough's card in the pane: the short version and its stops; a stop opens the tab there. */
export function WalkthroughCard(props: {
  entry: AgentBlockTranscriptEntry;
  session: ClientSession;
}): JSX.Element {
  const blockId = props.entry.block.id;
  const card = {
    kind: "walkthrough",
    conversationId: props.entry.conversationId ?? null,
    blockId,
  } as const;
  const walkthrough = () => agentBlock(props.session, props.entry.block.key)?.block.walkthrough;
  const collapsed = () => props.entry.asideCollapsed === true;
  const toggle = () => setCardCollapsed(props.session, card, !collapsed());
  const own = ownCardOnFocus(() => ({ session: props.session, card, toggle }));
  // A question asked here that waits on the user, for the row that opens the tab at its card.
  const waiting = () =>
    walkthroughQuestions(props.session, card.conversationId, blockId).find(({ entry }) =>
      (entry.asideEntries ?? []).some(hasPendingRequest),
    );
  const open = (target: WalkthroughTarget): void => {
    requestTarget(props.session, cardTabPath(card), target);
    void openCard(card);
  };

  return (
    <AgentCardShell
      label={`Walkthrough · ${props.entry.summary ?? blockId}`}
      collapsed={collapsed()}
      headOnly={collapsed() && waiting() === undefined}
      meta={null}
      wrapLabel={false}
      onToggle={toggle}
      toggleHint={liveKeyHint(CommandIds.toggleAgentAside)}
      actions={<AgentCardOpenButton card={card} />}
      onOwn={own}
      data={{ "data-agent-walkthrough": blockId }}
    >
      <Show when={walkthrough()}>
        {(read) => (
          <div class="walkthrough-card">
            <Show when={!collapsed()}>
              <AgentMarkdown
                attachable={false}
                cacheKey={read()}
                content={read().gist}
                flavor="agent"
                renderMermaid={false}
                session={props.session}
              />
              <For each={read().stops}>
                {(stop, index) => (
                  <StopRow
                    stop={stop}
                    index={index()}
                    title={`Open at Stop ${index() + 1}`}
                    onClick={() => open({ stopId: stop.id })}
                  />
                )}
              </For>
            </Show>
            <Show when={waiting()}>
              {(question) => (
                <button
                  type="button"
                  class="ui-row"
                  onClick={() => open({ question: question().entry.conversationId! })}
                >
                  <span class="ui-row-name">A question is waiting for you</span>
                </button>
              )}
            </Show>
          </div>
        )}
      </Show>
    </AgentCardShell>
  );
}
