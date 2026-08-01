import {
  createEffect,
  createMemo,
  createSignal,
  type JSX,
  on,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import type { AgentPaneUpdate } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
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
  reviewAdded: number;
  reviewFileCount: number;
  reviewRemoved: number;
  shortcut: string;
  onFocus: () => void;
}): JSX.Element {
  let bodyRef: HTMLDivElement | undefined;
  let scrollScheduled = false;
  let programmaticScroll = false;
  let assignedTop = 0;
  const [stickToBottom, setStickToBottom] = createSignal(true);
  const [turnStartAbove, setTurnStartAbove] = createSignal(false);
  // Feed <For> a keyed store: reconcile preserves each unchanged entry's proxy identity, so the row is reused and
  // AgentMarkdown re-parses only the entry whose text actually changed — heavy work stays O(changed), not
  // O(entries). toAgentTranscript itself is one light O(messages) scan per flush (batched in AgentPaneAccumulator).
  const [entries, setEntries] = createStore<AgentTranscriptEntry[]>([]);
  createEffect(() => setEntries(reconcile(toAgentTranscript(props.messages), { key: "id" })));
  const providerName = (): string => (props.providerId === "codex" ? "Codex" : "Agent");
  // Only the card the keyboard chords answer wears the chips.
  const keyboardApprovalId = createMemo(() => pendingApproval(props.messages)?.requestId ?? null);

  const isNearBottom = (): boolean => {
    if (bodyRef === undefined) {
      return true;
    }
    const distance = bodyRef.scrollHeight - bodyRef.scrollTop - bodyRef.clientHeight;
    const lineHeight = Number.parseFloat(getComputedStyle(bodyRef).lineHeight);
    return distance <= Math.ceil(lineHeight * 3);
  };

  const turnStart = (): HTMLElement | null =>
    bodyRef?.querySelector<HTMLElement>("[data-agent-turn-start]") ?? null;

  const updateTurnStartPosition = (): void => {
    const start = turnStart();
    setTurnStartAbove(
      bodyRef !== undefined &&
        start !== null &&
        start.getBoundingClientRect().top < bodyRef.getBoundingClientRect().top,
    );
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
      updateTurnStartPosition();
    });
  };

  const jumpToTurn = (): boolean => {
    const start = turnStart();
    if (bodyRef === undefined || start === null) {
      return false;
    }
    const top =
      bodyRef.scrollTop + start.getBoundingClientRect().top - bodyRef.getBoundingClientRect().top;
    if (Math.abs(bodyRef.scrollTop - top) < 1) {
      return false;
    }
    setStickToBottom(false);
    bodyRef.scrollTop = top;
    updateTurnStartPosition();
    return true;
  };

  const jumpToLatest = (): boolean => {
    if (bodyRef === undefined || (stickToBottom() && isNearBottom())) {
      return false;
    }
    setStickToBottom(true);
    scrollToBottom();
    return true;
  };

  // Our own scroll-to-bottom lands a frame after the assignment: chase content appended in between,
  // but a user scroll coalesced into the same event (scrollTop moved off the assigned spot) wins.
  const onBodyScroll = (): void => {
    if (programmaticScroll) {
      programmaticScroll = false;
      if (bodyRef !== undefined && bodyRef.scrollTop === assignedTop) {
        updateTurnStartPosition();
        if (!isNearBottom()) {
          scrollToBottom();
        }
        return;
      }
    }
    setStickToBottom(isNearBottom());
    updateTurnStartPosition();
  };

  // Follow content growth: props.messages changes once per publish (including text deltas), so this tracks the
  // transcript without depending on the reconciled store (which mutates in place). Isolated via `on` so a
  // stickToBottom flip doesn't re-scroll — that path scrolls explicitly (the follow pill / onBodyScroll handler).
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

  onMount(() => {
    const resizeObserver = new ResizeObserver(() => {
      if (stickToBottom()) {
        scrollToBottom();
      } else {
        updateTurnStartPosition();
      }
    });
    if (bodyRef !== undefined) {
      resizeObserver.observe(bodyRef);
      const transcript = bodyRef.querySelector<HTMLElement>("[data-agent-transcript]");
      if (transcript !== null) {
        resizeObserver.observe(transcript);
      }
    }
    const unregisterTurn = registerCommand(CommandIds.agentJumpToTurn, jumpToTurn);
    const unregisterLatest = registerCommand(CommandIds.agentJumpToLatest, jumpToLatest);
    onCleanup(() => {
      resizeObserver.disconnect();
      unregisterTurn();
      unregisterLatest();
    });
  });

  const commandTitle = (label: string, commandId: string): string => {
    const key = liveKeyLabel(commandId);
    return key === "" ? label : `${label} (${key})`;
  };

  const focusPromptIn = (surface: EventTarget | null): void => {
    props.onFocus();
    if (surface instanceof HTMLElement) {
      surface
        .querySelector<HTMLTextAreaElement>("[data-agent-composer] textarea")
        ?.focus({ preventScroll: true });
    }
  };
  const hasTextSelection = (): boolean => document.getSelection()?.isCollapsed === false;

  const focusPrompt = (event: MouseEvent): void => {
    if (event.button !== 0 || event.detail === 0) {
      return;
    }

    const target = event.target;
    if (
      target instanceof Element &&
      target.closest(
        "textarea, input, select, [contenteditable]:not([contenteditable='false'])",
      ) !== null
    ) {
      return;
    }
    if (hasTextSelection()) {
      return;
    }
    focusPromptIn(event.currentTarget);
  };

  const focusPromptFromDisabled = (event: PointerEvent): void => {
    if (
      event.button === 0 &&
      event.target instanceof Element &&
      event.target.closest(":disabled") !== null &&
      !hasTextSelection()
    ) {
      focusPromptIn(event.currentTarget);
    }
  };

  return (
    // biome-ignore lint/a11y/noStaticElementInteractions: Surface clicks are a pointer convenience; the textarea remains keyboard-focusable.
    // biome-ignore lint/a11y/useKeyWithClickEvents: Keyboard activation already keeps the activated control focused.
    <div
      class="agent-surface"
      classList={{ active: props.active }}
      data-kind="terminal:claude"
      data-surface="structured-agent"
      onClick={focusPrompt}
      onPointerUp={focusPromptFromDisabled}
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
            entries={entries}
            keyboardApprovalId={keyboardApprovalId()}
            messages={props.messages}
            providerName={providerName()}
            slot={props.slot}
          />
        </div>
        <Show when={stickToBottom() && turnStartAbove()}>
          <button
            type="button"
            class="agent-follow-pill"
            title={commandTitle("Jump to the start of this turn", CommandIds.agentJumpToTurn)}
            onClick={() => jumpToTurn()}
          >
            ↑ Jump to turn
          </button>
        </Show>
        <Show when={!stickToBottom()}>
          <button
            type="button"
            class="agent-follow-pill"
            title={commandTitle(
              "Scroll to the latest activity and follow it",
              CommandIds.agentJumpToLatest,
            )}
            onClick={() => jumpToLatest()}
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
          if (isNearBottom()) {
            setStickToBottom(true);
          }
        }}
      />
      <AgentStatusLine
        backendId={props.backendId}
        reviewAdded={props.reviewAdded}
        reviewFileCount={props.reviewFileCount}
        reviewRemoved={props.reviewRemoved}
        slot={props.slot}
      />
    </div>
  );
}
