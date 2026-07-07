import { createEffect, createMemo, createSignal, For, type JSX, Show } from "solid-js";
import { type AgentPaneUpdate, postToHost } from "../bridge";
import { sendPastedImagesFromClipboard } from "../terminal/paste-image";
import { ApprovalActions, EditLocationActions, InputRequestActions } from "./AgentPaneActions";

export function AgentPane(props: {
  slot: string | null;
  providerId: "claude" | "codex" | null;
  active: boolean;
  messages: AgentPaneUpdate[];
  shortcut: string;
  onFocus: () => void;
}): JSX.Element {
  const [draft, setDraft] = createSignal("");
  const [lastDraftIndex, setLastDraftIndex] = createSignal(-1);
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
  const visibleMessages = createMemo(() =>
    props.messages.filter((message) => message.type !== "draft"),
  );

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
    <div class="agent-surface" classList={{ active: props.active }} data-kind="agent">
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
      <div class="agent-body">
        <For each={visibleMessages()}>
          {(message) => (
            <article class={`agent-card agent-card-${message.type}`}>
              <header class="agent-card-head">
                <span>{message.itemType ?? message.type}</span>
                <Show when={message.status !== null && message.status !== undefined}>
                  <small>{message.status}</small>
                </Show>
              </header>
              <Show when={message.summary !== null && message.summary !== undefined}>
                <div class="agent-card-summary">{message.summary}</div>
              </Show>
              <Show when={message.text !== null && message.text !== undefined}>
                <pre class="agent-card-text">{message.text}</pre>
              </Show>
              <Show when={message.type === "approval-requested"}>
                <ApprovalActions slot={props.slot} requestId={message.itemId} />
              </Show>
              <Show when={message.type === "input-requested"}>
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
