import { createEffect, createMemo, type JSX, Show } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentComposer } from "./AgentComposer";
import { toAgentTranscript } from "./AgentPaneMessages";
import { createAgentPaneScroll } from "./AgentPaneScroll";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentStatusLine } from "./AgentStatusLine";
import { AgentTranscript } from "./AgentTranscript";
import { pendingApproval } from "./turn-progress";

export function AgentPane(props: {
  inputProtocol: number;
  session: ClientSession | null;
  providerId: "claude" | "codex" | null;
  active: boolean;
  messages: AgentPaneUpdate[];
  reviewAdded: number;
  reviewFileCount: number;
  reviewRemoved: number;
  shortcut: string;
  onFocus: () => void;
}): JSX.Element {
  // Feed <For> a keyed store: reconcile preserves each unchanged entry's proxy identity, so the row is reused and
  // AgentMarkdown re-parses only the entry whose text actually changed — heavy work stays O(changed), not
  // O(entries). toAgentTranscript itself is one light O(messages) scan per flush (batched in AgentPaneAccumulator).
  const [entries, setEntries] = createStore<AgentTranscriptEntry[]>([]);
  createEffect(() => setEntries(reconcile(toAgentTranscript(props.messages), { key: "id" })));
  const providerName = (): string => (props.providerId === "codex" ? "Codex" : "Agent");
  // Only the card the keyboard chords answer wears the chips.
  const keyboardApprovalId = createMemo(() => pendingApproval(props.messages)?.requestId ?? null);
  const scroll = createAgentPaneScroll(() => props.session);

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
        <div class="agent-body" ref={scroll.bindBody} onScroll={scroll.onScroll}>
          <div class="agent-transcript" data-agent-transcript>
            <AgentTranscript
              entries={entries}
              keyboardApprovalId={keyboardApprovalId()}
              messages={props.messages}
              providerName={providerName()}
              session={props.session}
            />
          </div>
        </div>
        <Show when={scroll.followingLatest() && scroll.turnStartAbove()}>
          <button
            type="button"
            class="agent-follow-pill"
            title={commandTitle("Jump to the start of this turn", CommandIds.agentJumpToTurn)}
            onClick={() => scroll.jumpToTurn()}
          >
            ↑ Jump to turn
          </button>
        </Show>
        <Show when={!scroll.followingLatest()}>
          <button
            type="button"
            class="agent-follow-pill"
            title={commandTitle(
              "Scroll to the latest activity and follow it",
              CommandIds.agentJumpToLatest,
            )}
            onClick={() => scroll.jumpToLatest()}
          >
            ↓ Jump to latest
          </button>
        </Show>
      </div>
      <AgentComposer
        active={props.active}
        inputProtocol={props.inputProtocol}
        messages={props.messages}
        session={props.session}
        onSubmitted={scroll.followIfNearBottom}
      />
      <AgentStatusLine
        reviewAdded={props.reviewAdded}
        reviewFileCount={props.reviewFileCount}
        reviewRemoved={props.reviewRemoved}
        session={props.session}
      />
    </div>
  );
}
