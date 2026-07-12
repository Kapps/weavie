import { createEffect, createMemo, createSignal, type JSX, on, Show } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import type { AgentPaneUpdate } from "../bridge";
import { AgentComposer } from "./AgentComposer";
import { toAgentTranscript } from "./AgentPaneMessages";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
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
  const [stickToBottom, setStickToBottom] = createSignal(true);
  // Feed <For> a keyed store: reconcile preserves each unchanged entry's proxy identity, so the row is reused and
  // AgentMarkdown re-parses only the entry whose text actually changed — heavy work stays O(changed), not
  // O(entries). toAgentTranscript itself is one light O(messages) scan per flush (batched in AgentPaneAccumulator).
  const [entries, setEntries] = createStore<AgentTranscriptEntry[]>([]);
  createEffect(() => setEntries(reconcile(toAgentTranscript(props.messages), { key: "id" })));
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
      if (bodyRef !== undefined) {
        bodyRef.scrollTop = bodyRef.scrollHeight;
      }
    });
  };

  // Follow content growth: props.messages changes once per publish (including text deltas), so this tracks the
  // transcript without depending on the reconciled store (which mutates in place). Isolated via `on` so a
  // stickToBottom flip doesn't re-scroll — that path scrolls explicitly (the follow pill / onScroll handler).
  createEffect(
    on(
      () => props.messages,
      () => {
        if (stickToBottom()) {
          scrollToBottom();
        }
      },
    ),
  );

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
        <div class="agent-body" ref={bodyRef} onScroll={() => setStickToBottom(isAtBottom())}>
          <AgentTranscript
            entries={entries}
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
