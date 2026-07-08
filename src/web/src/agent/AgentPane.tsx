import { createEffect, createMemo, createSignal, For, type JSX, Show } from "solid-js";
import { type AgentPaneUpdate, postToHost } from "../bridge";
import { sendPastedImagesFromClipboard } from "../terminal/paste-image";
import { ApprovalActions, EditLocationActions, InputRequestActions } from "./AgentPaneActions";
import { toVisibleAgentMessages } from "./AgentPaneMessages";

export function AgentPane(props: {
  slot: string | null;
  providerId: "claude" | "codex" | null;
  active: boolean;
  messages: AgentPaneUpdate[];
  shortcut: string;
  onFocus: () => void;
}): JSX.Element {
  let bodyRef: HTMLDivElement | undefined;
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
  const visibleMessages = createMemo(() => toVisibleAgentMessages(props.messages));

  const isNearBottom = (): boolean => {
    if (bodyRef === undefined) {
      return true;
    }

    return bodyRef.scrollHeight - bodyRef.scrollTop - bodyRef.clientHeight <= 96;
  };

  const scrollToBottom = (): void => {
    queueMicrotask(() => {
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
    visibleMessages();
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
        <For each={visibleMessages()}>
          {(message) => (
            <article class={`agent-card agent-card-${message.type}`}>
              <header class="agent-card-head">
                <span>{message.displayType}</span>
                <Show when={message.displayStatus !== null}>
                  <small>{message.displayStatus}</small>
                </Show>
              </header>
              <Show when={message.displaySummary !== null}>
                <div class="agent-card-summary">{message.displaySummary}</div>
              </Show>
              <Show when={message.displayText !== null}>
                <pre class="agent-card-text">{message.displayText}</pre>
              </Show>
              <Show
                when={message.type === "approval-requested" && message.displayStatus === "pending"}
              >
                <ApprovalActions slot={props.slot} requestId={message.itemId} />
              </Show>
              <Show
                when={message.type === "input-requested" && message.displayStatus === "pending"}
              >
                <InputRequestActions slot={props.slot} message={message} />
              </Show>
              <Show when={message.type === "edit-location"}>
                <EditLocationActions target={message.text} />
              </Show>
            </article>
          )}
        </For>
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
          placeholder="Ask Codex…"
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
