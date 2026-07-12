import { createEffect, createMemo, createSignal, type JSX, Show } from "solid-js";
import type { AgentPaneUpdate } from "../bridge";
import { AgentComposer } from "./AgentComposer";
import { toAgentTranscript } from "./AgentPaneMessages";
import { AgentStatusLine } from "./AgentStatusLine";
import { AgentTranscript } from "./AgentTranscript";
import { pendingApproval } from "./turn-progress";

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
  let programmaticScroll = false;
  let assignedTop = 0;
  const [stickToBottom, setStickToBottom] = createSignal(true);
  const transcript = createMemo(() => toAgentTranscript(props.messages));
  const providerName = (): string => (props.providerId === "codex" ? "Codex" : "Agent");
  // Only the card the keyboard chords answer wears the chips.
  const keyboardApprovalId = createMemo(() => pendingApproval(props.messages)?.requestId ?? null);

  const isAtBottom = (): boolean => {
    if (bodyRef === undefined) {
      return true;
    }
    const distance = bodyRef.scrollHeight - bodyRef.scrollTop - bodyRef.clientHeight;
    // Allow only rounding noise. A generous "near bottom" threshold makes opening a disclosure or
    // scrolling slightly upward opt back into auto-scroll, which yanks the content out from under the user.
    return distance <= 4;
  };

  const scrollToBottom = (): void => {
    if (scrollScheduled) {
      return;
    }
    scrollScheduled = true;
    requestAnimationFrame(() => {
      scrollScheduled = false;
      if (bodyRef === undefined || !stickToBottom()) {
        return;
      }
      const previous = bodyRef.scrollTop;
      bodyRef.scrollTop = bodyRef.scrollHeight;
      assignedTop = bodyRef.scrollTop;
      programmaticScroll = assignedTop !== previous;
    });
  };

  // Our own scroll-to-bottom lands a frame after the assignment: chase content appended in between,
  // but a user scroll coalesced into the same event (scrollTop moved off the assigned spot) wins.
  const onBodyScroll = (): void => {
    if (programmaticScroll) {
      programmaticScroll = false;
      if (bodyRef !== undefined && bodyRef.scrollTop === assignedTop) {
        if (!isAtBottom()) {
          scrollToBottom();
        }
        return;
      }
    }
    setStickToBottom(isAtBottom());
  };

  createEffect(() => {
    transcript();
    if (stickToBottom()) {
      scrollToBottom();
    }
  });

  const focusPrompt = (event: MouseEvent): void => {
    if (event.button !== 0) {
      return;
    }

    const target = event.target;
    if (
      target instanceof Element &&
      target.closest("button, textarea, input, select, a, [contenteditable='true'], [tabindex]") !==
        null
    ) {
      return;
    }

    props.onFocus();
    if (event.currentTarget instanceof HTMLElement) {
      event.currentTarget
        .querySelector<HTMLTextAreaElement>("[data-agent-composer] textarea")
        ?.focus({ preventScroll: true });
    }
  };

  return (
    // biome-ignore lint/a11y/noStaticElementInteractions: Surface clicks are a pointer convenience; the textarea remains keyboard-focusable.
    <div
      class="agent-surface"
      classList={{ active: props.active }}
      data-kind="terminal:claude"
      data-surface="structured-agent"
      onMouseDown={focusPrompt}
    >
      <div class="pane-head" role="toolbar">
        <span class="pane-label">{providerName()}</span>
        <Show when={props.shortcut !== ""}>
          <span class="pane-shortcut">{props.shortcut}</span>
        </Show>
      </div>
      <div class="agent-body-wrap">
        <div class="agent-body" ref={bodyRef} onScroll={onBodyScroll}>
          <AgentTranscript
            entries={transcript()}
            keyboardApprovalId={keyboardApprovalId()}
            messages={props.messages}
            providerName={providerName()}
            slot={props.slot}
          />
        </div>
        <Show when={!stickToBottom()}>
          <button
            type="button"
            class="agent-follow-pill"
            title="Scroll to the latest activity and follow it"
            onClick={() => {
              setStickToBottom(true);
              scrollToBottom();
            }}
          >
            ↓ Jump to latest
          </button>
        </Show>
      </div>
      <AgentComposer
        active={props.active}
        backendId={props.backendId}
        inputProtocol={props.inputProtocol}
        messages={props.messages}
        slot={props.slot}
        onSubmitted={() => {
          if (isAtBottom()) {
            setStickToBottom(true);
          }
        }}
      />
      <AgentStatusLine backendId={props.backendId} slot={props.slot} />
    </div>
  );
}
