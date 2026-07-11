import { createEffect, createMemo, createSignal, type JSX, Show } from "solid-js";
import type { AgentPaneUpdate } from "../bridge";
import { AgentComposer } from "./AgentComposer";
import { toAgentTranscript } from "./AgentPaneMessages";
import { AgentStatusLine } from "./AgentStatusLine";
import { AgentTranscript } from "./AgentTranscript";

export function AgentPane(props: {
  backendId: string;
  inputProtocol: number;
  slot: string | null;
  providerId: "claude" | "codex" | null;
  active: boolean;
  messages: AgentPaneUpdate[];
  shortcut: string;
  onFocus: () => void;
}): JSX.Element {
  let bodyRef: HTMLDivElement | undefined;
  let scrollScheduled = false;
  const [stickToBottom, setStickToBottom] = createSignal(true);
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
    transcript();
    if (stickToBottom()) {
      scrollToBottom();
    }
  });

  return (
    <div
      class="agent-surface"
      classList={{ active: props.active }}
      data-kind="terminal:claude"
      data-surface="structured-agent"
    >
      <div
        class="pane-head"
        role="toolbar"
        onMouseDown={(event) => {
          event.preventDefault();
          props.onFocus();
        }}
      >
        <span class="pane-label">{props.providerId === "codex" ? "Codex" : "Agent"}</span>
        <Show when={props.shortcut !== ""}>
          <span class="pane-shortcut">{props.shortcut}</span>
        </Show>
      </div>
      <div class="agent-body" ref={bodyRef} onScroll={() => setStickToBottom(isNearBottom())}>
        <AgentTranscript entries={transcript()} slot={props.slot} />
      </div>
      <AgentComposer
        active={props.active}
        backendId={props.backendId}
        inputProtocol={props.inputProtocol}
        messages={props.messages}
        slot={props.slot}
        onSubmitted={() => {
          if (isNearBottom()) {
            setStickToBottom(true);
          }
        }}
      />
      <AgentStatusLine backendId={props.backendId} slot={props.slot} />
    </div>
  );
}
