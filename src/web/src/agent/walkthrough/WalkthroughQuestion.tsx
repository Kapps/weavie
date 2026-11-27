import { CircleStop, X } from "lucide-solid";
import { type JSX, Show } from "solid-js";
import type { ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import { AgentCardReplyBox } from "../AgentCardReplyBox";
import { AgentCardShell } from "../AgentCardShell";
import { NestedTranscript } from "../AgentNestedCard";
import type { WalkthroughQuestion as Question } from "../AgentPaneModel";
import { ownCardOnFocus, setCardCollapsed } from "../agent-cards";
import { replyComposer } from "../composer-store";
import { createNestedExpansion } from "../nested-expansion";
import { stopAside } from "./walkthrough-ask";

/** A question asked from a walkthrough, under what was selected: its side conversation, Reply, Stop and Dismiss. */
export function WalkthroughQuestion(props: {
  question: Question;
  session: ClientSession;
  /** Shown when the text it was asked about has since changed. */
  moved: boolean;
}): JSX.Element {
  const entry = () => props.question.entry;
  const conversationId = entry().conversationId!;
  const card = { kind: "aside", conversationId } as const;
  const expansion = createNestedExpansion(props.session);
  const dismiss = () => setCardCollapsed(props.session, card, true);
  const own = ownCardOnFocus(() => ({ session: props.session, card, toggle: dismiss }));
  return (
    <div class="walkthrough-question" data-comment-box data-walkthrough-question={conversationId}>
      <AgentCardShell
        label={entry().summary ?? "Question"}
        collapsed={false}
        headOnly={false}
        wrapLabel={true}
        meta={
          <Show when={entry().asideActive === true}>
            <small>Working</small>
          </Show>
        }
        onToggle={dismiss}
        toggleHint=""
        actions={
          <>
            <Show when={entry().asideActive === true}>
              <button
                type="button"
                class="ui-icon-button"
                title="Stop"
                aria-label="Stop"
                onClick={() => stopAside(props.session, conversationId)}
              >
                <CircleStop class="ui-icon" />
              </button>
            </Show>
            <button
              type="button"
              class="ui-icon-button"
              title="Dismiss"
              aria-label="Dismiss"
              onClick={dismiss}
            >
              <X class="ui-icon" />
            </button>
          </>
        }
        onOwn={own}
        data={{}}
      >
        <Show when={props.moved}>
          <p class="walkthrough-moved">Asked about text that has since changed</p>
        </Show>
        <blockquote class="walkthrough-quote">{props.question.quote}</blockquote>
        <NestedTranscript
          pendingOnly={false}
          entries={(entry().asideEntries ?? []).filter(
            (row, index) => index > 0 || row.tone !== "user",
          )}
          expandedDetails={expansion.expandedDetails()}
          keyboardRequestKey={null}
          onUpdatesToggle={expansion.onUpdatesToggle}
          onDetailsToggle={expansion.onDetailsToggle}
          session={props.session}
        />
        <Show when={entry().asideReplyable !== false}>
          <AgentCardReplyBox
            session={props.session}
            composer={replyComposer(props.session, conversationId)}
            label="Reply"
            replyTitle="Reply"
            hint={`Paste text or images${keyHint(CommandIds.agentPaste)}`}
            disabled={entry().asideActive === true}
          />
        </Show>
      </AgentCardShell>
    </div>
  );
}
