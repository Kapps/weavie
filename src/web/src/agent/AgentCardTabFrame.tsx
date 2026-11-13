import { createMemo, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import type { TabKind } from "../editor/tab-entry";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { cardForTabEntry } from "./card-tab";
import { agentPaneModel } from "./pane-store";

/** A card's editor tab: its live entry, or, once history has loaded without it, a notice that it is gone. */
export function AgentCardTabFrame(props: {
  session: ClientSession;
  kind: TabKind;
  path: string;
  /** What the notice calls the card: "conversation", "dashboard", "block". */
  noun: string;
  class: string;
  bind(element: HTMLElement): () => void;
  children: (entry: () => AgentTranscriptEntry) => JSX.Element;
}): JSX.Element {
  let host!: HTMLDivElement;
  const entry = createMemo(() => cardForTabEntry(props.session, props.kind, props.path));
  onMount(() => onCleanup(props.bind(host)));
  return (
    <div
      class={`editor-agent-view agent-body ${props.class}`}
      data-kind="editor"
      tabindex="0"
      ref={host}
    >
      <Show
        when={entry()}
        fallback={
          <Show when={agentPaneModel(props.session)?.historyComplete()}>
            <div class="editor-plan-notice">This {props.noun} is no longer in the transcript.</div>
          </Show>
        }
      >
        {(card) => props.children(card)}
      </Show>
    </div>
  );
}
