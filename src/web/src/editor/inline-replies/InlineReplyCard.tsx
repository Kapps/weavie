import { X } from "lucide-solid";
import { createMemo, type JSX, Match, Show, Switch } from "solid-js";
import { AgentAsideReplyBox } from "../../agent/AgentAsideReplyBox";
import { AgentCardShell } from "../../agent/AgentCardShell";
import { NestedTranscript } from "../../agent/AgentNestedCard";
import { hasPendingRequest } from "../../agent/AgentPendingRequests";
import { sameItems } from "../../agent/AgentTranscriptSnapshot";
import { AgentWorkingStatus } from "../../agent/AgentWorkingStatus";
import { agentQueuedSubmissions } from "../../agent/agent-queue-store";
import { CollapsibleCardContent } from "../../agent/CollapsibleCardContent";
import { hasSubmission, submissionReply } from "../../agent/inline-reply";
import { createNestedExpansion } from "../../agent/nested-expansion";
import { agentPaneModel } from "../../agent/pane-store";
import type { ClientSession } from "../../bridge";
import {
  dismissInlineReply,
  type InlineReply,
  inlineFollowUp,
  setInlineReplyCollapsed,
} from "./inline-replies-store";

/** One prompt sent from a selection, in the pane's card with the pane's rows for what the agent did about it. */
export function InlineReplyCard(props: {
  reply: InlineReply;
  session: ClientSession;
}): JSX.Element {
  const pane = () => agentPaneModel(props.session);
  const expansion = createNestedExpansion(props.session);
  // Unchanged entries keep their identity across snapshots, so a settled reply never re-renders.
  const answer = createMemo(
    () => {
      const model = pane();
      return model === null
        ? null
        : submissionReply(model.transcript(), props.reply.id, props.reply.followUps);
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
  const entries = () => answer()?.entries ?? [];
  // The card's prompts the transcript does not hold yet, and whether the host has one of them queued.
  const undelivered = createMemo(() => {
    const model = pane();
    return model === null
      ? []
      : [props.reply.id, ...props.reply.followUps].filter(
          (id) => !hasSubmission(model.transcript(), id),
        );
  });
  const queued = () =>
    agentQueuedSubmissions(props.session).some((waiting) => undelivered().includes(waiting.id));
  const collapsed = () => props.reply.collapsed;
  return (
    <AgentCardShell
      label={props.reply.question}
      collapsed={collapsed()}
      headOnly={collapsed() && !entries().some(hasPendingRequest)}
      meta={
        <Switch>
          <Match when={props.reply.error !== null}>
            <small class="inline-reply-error">Not sent</small>
          </Match>
          <Match when={answer()?.working}>
            <AgentWorkingStatus
              compact={false}
              interruptHint={false}
              pendingKind={pane()?.pendingRequestKind() ?? null}
              turnActive={true}
              turnStartedAt={pane()?.turnStartedAt() ?? null}
            />
          </Match>
          <Match when={queued()}>
            <small>Queued</small>
          </Match>
          {/* A transcript still loading says nothing yet about a prompt it has not shown. */}
          <Match when={undelivered().length > 0 && pane()?.historyComplete()}>
            <small>Sending</small>
          </Match>
          <Match when={answer() !== null && entries().length === 0}>
            <small>No reply</small>
          </Match>
        </Switch>
      }
      onToggle={() => setInlineReplyCollapsed(props.session, props.reply.id, !collapsed())}
      toggleHint=""
      actions={
        <button
          type="button"
          class="ui-icon-button"
          aria-label="Dismiss"
          title="Dismiss"
          onClick={() => dismissInlineReply(props.session, props.reply.id)}
        >
          <X aria-hidden="true" />
        </button>
      }
      onOwn={() => {}}
      data={{ "data-inline-reply": props.reply.id }}
    >
      <div class="inline-reply-body agent-body">
        <Show when={props.reply.error}>
          {(error) => (
            <p class="inline-reply-error" role="alert">
              {error()}
            </p>
          )}
        </Show>
        <NestedTranscript
          pendingOnly={collapsed()}
          entries={entries()}
          expandedDetails={expansion.expandedDetails()}
          keyboardRequestKey={null}
          onUpdatesToggle={expansion.onUpdatesToggle}
          onDetailsToggle={expansion.onDetailsToggle}
          session={props.session}
        />
      </div>
      <Show when={answer() !== null}>
        <CollapsibleCardContent collapsed={collapsed()}>
          <AgentAsideReplyBox
            session={props.session}
            composer={inlineFollowUp(props.session, props.reply)}
            label="Reply"
            disabled={false}
          />
        </CollapsibleCardContent>
      </Show>
    </AgentCardShell>
  );
}
