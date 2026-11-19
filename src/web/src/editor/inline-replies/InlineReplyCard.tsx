import { X } from "lucide-solid";
import { createMemo, type JSX, Match, Show, Switch } from "solid-js";
import { AgentMarkdown } from "../../agent/AgentMarkdown";
import { sameItems } from "../../agent/AgentTranscriptSnapshot";
import { AgentWorkingStatus } from "../../agent/AgentWorkingStatus";
import { agentQueuedSubmissions } from "../../agent/agent-queue-store";
import { ForEachId } from "../../agent/ForEachId";
import { submissionReply } from "../../agent/inline-reply";
import { agentPaneModel } from "../../agent/pane-store";
import type { ClientSession } from "../../bridge";
import { dismissInlineReply, type InlineReply } from "./inline-replies-store";

const Problem = (props: { text: string | null }): JSX.Element => (
  <p class="inline-reply-error" role="alert">
    {props.text}
  </p>
);

const Status = (props: { text: string }): JSX.Element => (
  <span class="inline-reply-status" role="status">
    {props.text}
  </span>
);

/** One prompt sent from a selection and what the agent has said to it so far. */
export function InlineReplyCard(props: {
  reply: InlineReply;
  session: ClientSession;
}): JSX.Element {
  const pane = () => agentPaneModel(props.session);
  // Unchanged entries keep their identity across snapshots, so a settled reply never re-renders.
  const answer = createMemo(
    () => {
      const model = pane();
      return model === null ? null : submissionReply(model.unfolded(), props.reply.id);
    },
    null,
    {
      equals: (before, after) =>
        before === after ||
        (before !== null &&
          after !== null &&
          before.working === after.working &&
          sameItems(before.entries, after.entries)),
    },
  );
  const queued = () =>
    agentQueuedSubmissions(props.session).some((waiting) => waiting.id === props.reply.id);
  return (
    <section class="inline-reply ui-card" data-inline-reply aria-label="Agent reply">
      <header class="inline-reply-head">
        <span class="inline-reply-question" title={props.reply.question}>
          {props.reply.question}
        </span>
        <button
          type="button"
          class="ui-icon-button"
          aria-label="Dismiss"
          title="Dismiss"
          onClick={() => dismissInlineReply(props.session, props.reply.id)}
        >
          <X aria-hidden="true" />
        </button>
      </header>
      <Switch>
        <Match when={props.reply.error}>{(error) => <Problem text={error()} />}</Match>
        <Match when={answer()}>
          {(reply) => (
            <>
              <ForEachId each={reply().entries}>
                {(entry) => (
                  <Show
                    when={entry().tone !== "error"}
                    fallback={<Problem text={entry().text ?? entry().summary} />}
                  >
                    <AgentMarkdown
                      attachable={false}
                      cacheKey={entry()}
                      content={entry().text ?? ""}
                      flavor="agent"
                      renderMermaid={!entry().streaming}
                      session={props.session}
                    />
                  </Show>
                )}
              </ForEachId>
              <Show when={!reply().working && reply().entries.length === 0}>
                <Status text="No reply" />
              </Show>
              <AgentWorkingStatus
                compact={false}
                interruptHint={false}
                pendingKind={pane()?.pendingRequestKind() ?? null}
                turnActive={reply().working}
                turnStartedAt={pane()?.turnStartedAt() ?? null}
              />
            </>
          )}
        </Match>
        {/* A transcript still loading says nothing yet about a prompt it has not shown. */}
        <Match when={pane()?.historyComplete()}>
          <Status text={queued() ? "Queued" : "Sending"} />
        </Match>
      </Switch>
    </section>
  );
}
