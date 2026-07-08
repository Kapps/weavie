import { createEffect, createMemo, createSignal, For, type JSX, Show } from "solid-js";
import { type AgentPaneUpdate, postToHost } from "../bridge";
import { sendPastedImagesFromClipboard } from "../terminal/paste-image";
import { ApprovalActions, EditLocationActions, InputRequestActions } from "./AgentPaneActions";
import { toAgentTranscript } from "./AgentPaneMessages";
import type { AgentActivityStep, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export function AgentPane(props: {
  slot: string | null;
  providerId: "claude" | "codex" | null;
  active: boolean;
  messages: AgentPaneUpdate[];
  shortcut: string;
  onFocus: () => void;
}): JSX.Element {
  let bodyRef: HTMLDivElement | undefined;
  let scrollScheduled = false;
  const [draft, setDraft] = createSignal("");
  const [lastDraftIndex, setLastDraftIndex] = createSignal(-1);
  const [stickToBottom, setStickToBottom] = createSignal(true);
  const pendingImages = createMemo(() =>
    props.messages.reduce((count, message) => {
      if (message.type !== "user-image") {
        return count;
      }
      if (message.status === "attached") {
        return count + 1;
      }
      return message.status === "submitted" ? Math.max(0, count - 1) : count;
    }, 0),
  );
  const canSubmit = createMemo(
    () => props.slot !== null && (draft().trim().length > 0 || pendingImages() > 0),
  );
  const transcript = createMemo(() => toAgentTranscript(props.messages));

  const isNearBottom = (): boolean => {
    if (bodyRef === undefined) {
      return true;
    }

    const distance = bodyRef.scrollHeight - bodyRef.scrollTop - bodyRef.clientHeight;
    return distance <= Math.max(160, bodyRef.clientHeight * 0.18);
  };

  const scrollToBottom = (): void => {
    if (scrollScheduled) {
      return;
    }

    scrollScheduled = true;
    requestAnimationFrame(() => {
      scrollScheduled = false;
      if (bodyRef !== undefined) {
        bodyRef.scrollTop = bodyRef.scrollHeight;
      }
    });
  };

  createEffect(() => {
    const messages = props.messages;
    for (let i = messages.length - 1; i >= 0; i--) {
      const message = messages[i];
      if (message !== undefined && message.type === "draft" && i !== lastDraftIndex()) {
        setLastDraftIndex(i);
        setDraft(message.text ?? "");
        break;
      }
    }
  });

  createEffect(() => {
    transcript();
    if (stickToBottom()) {
      scrollToBottom();
    }
  });

  const submit = (): void => {
    const slot = props.slot;
    const prompt = draft().trim();
    if (slot === null || (prompt.length === 0 && pendingImages() === 0)) {
      return;
    }
    if (isNearBottom()) {
      setStickToBottom(true);
    }
    setDraft("");
    postToHost({ type: "agent-submit", slot, prompt });
  };

  const interrupt = (): void => {
    const slot = props.slot;
    if (slot !== null) {
      postToHost({ type: "agent-interrupt", slot });
    }
  };

  return (
    <div class="agent-surface" classList={{ active: props.active }} data-kind="terminal:claude">
      <div
        class="pane-head"
        role="toolbar"
        onMouseDown={(event) => {
          event.preventDefault();
          props.onFocus();
        }}
      >
        <span class="pane-label">{props.providerId === "codex" ? "Codex" : "Agent"}</span>
        <span class="pane-shortcut">{props.shortcut}</span>
      </div>
      <div class="agent-body" ref={bodyRef} onScroll={() => setStickToBottom(isNearBottom())}>
        <Show
          when={transcript().length > 0}
          fallback={
            <div class="agent-empty">codex is idle - ask for a plan, a change, or a review.</div>
          }
        >
          <For each={transcript()}>
            {(entry) => <TranscriptEntry entry={entry} slot={props.slot} />}
          </For>
        </Show>
      </div>
      <form
        class="agent-compose"
        onSubmit={(event) => {
          event.preventDefault();
          submit();
        }}
      >
        <textarea
          value={draft()}
          placeholder="Ask Codex..."
          onInput={(event) => setDraft(event.currentTarget.value)}
          onPaste={(event) => {
            const slot = props.slot;
            if (slot !== null) {
              sendPastedImagesFromClipboard(event, slot);
            }
          }}
          onKeyDown={(event) => {
            if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
              event.preventDefault();
              submit();
            }
          }}
        />
        <div class="agent-compose-actions">
          <button type="button" title="Interrupt" onClick={interrupt}>
            Interrupt
          </button>
          <button type="submit" title="Send (Ctrl+Enter)" disabled={!canSubmit()}>
            Send
          </button>
        </div>
      </form>
    </div>
  );
}

function TranscriptEntry(props: { entry: AgentTranscriptEntry; slot: string | null }): JSX.Element {
  return (
    <article class={`agent-entry agent-entry-${props.entry.kind} agent-tone-${props.entry.tone}`}>
      <div class="agent-entry-mark">{entryMark(props.entry)}</div>
      <div class="agent-entry-main">
        <header class="agent-entry-head">
          <span class="agent-entry-label">{props.entry.label}</span>
          <Show when={props.entry.status !== null}>
            <small>{props.entry.status}</small>
          </Show>
        </header>
        <Show when={props.entry.summary !== null}>
          <div class="agent-entry-summary">{props.entry.summary}</div>
        </Show>
        <Show when={props.entry.text !== null}>
          <pre class="agent-entry-text">{props.entry.text}</pre>
        </Show>
        <Show when={props.entry.details.length > 0}>
          <ActivityDetails steps={props.entry.details} />
        </Show>
        <EntryActions entry={props.entry} slot={props.slot} />
      </div>
    </article>
  );
}

function EntryActions(props: { entry: AgentTranscriptEntry; slot: string | null }): JSX.Element {
  const message = props.entry.actionMessage;
  if (message === null) {
    return null;
  }

  if (message.type === "approval-requested" && props.entry.status === "pending") {
    return <ApprovalActions slot={props.slot} requestId={message.itemId} />;
  }

  if (message.type === "input-requested" && props.entry.status === "pending") {
    return <InputRequestActions slot={props.slot} message={message} />;
  }

  if (message.type === "edit-location") {
    return <EditLocationActions target={message.text} />;
  }

  return null;
}

function ActivityDetails(props: { steps: AgentActivityStep[] }): JSX.Element {
  return (
    <details class="agent-activity-details">
      <summary>{props.steps.length === 1 ? "details" : `${props.steps.length} details`}</summary>
      <div class="agent-activity-list">
        <For each={props.steps}>
          {(step) => (
            <div class={`agent-activity-step agent-step-${step.tone}`}>
              <span class="agent-step-status">{step.status ?? "done"}</span>
              <span class="agent-step-label">{step.label}</span>
              <Show when={step.detailText !== null}>
                <pre>{step.detailText}</pre>
              </Show>
            </div>
          )}
        </For>
      </div>
    </details>
  );
}

function entryMark(entry: AgentTranscriptEntry): string {
  switch (entry.tone) {
    case "assistant":
      return "*";
    case "error":
      return "!";
    case "pending":
      return "?";
    case "user":
      return ">";
    case "warning":
      return "^";
    default:
      return ".";
  }
}
