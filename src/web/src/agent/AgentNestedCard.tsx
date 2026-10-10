import { For, type JSX, onCleanup, onMount } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import { newestVisibleAgentElement } from "./AgentViewport";

const toggles = new Map<HTMLElement, () => void>();

/** Collapses or expands the focused nested card, else the newest visible one. */
export function toggleAgentAside(): boolean {
  const active = document.querySelector(".agent-surface.active");
  const focused = document.activeElement?.closest<HTMLElement>(".agent-aside");
  const target =
    focused && active?.contains(focused) ? focused : newestVisibleAgentElement(".agent-aside");
  const toggle = target === undefined ? undefined : toggles.get(target);
  if (toggle === undefined) return false;
  toggle();
  return true;
}

/** The collapsible frame shared by BTW and subagent cards: a header, a nested transcript, and a footer. */
export function AgentNestedCard(props: {
  attributes: Record<string, string>;
  children?: JSX.Element;
  collapsed: boolean;
  entries: readonly AgentTranscriptEntry[];
  expandedDetails: ReadonlySet<string>;
  head: JSX.Element;
  headActions?: JSX.Element;
  keyboardRequestKey: string | null;
  label: string;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  onToggle: () => void;
  session: ClientSession;
}): JSX.Element {
  let card: HTMLElement | undefined;
  const toggleLabel = () => `${props.collapsed ? "Expand" : "Collapse"} ${props.label}`;
  const toggleTitle = () => {
    const key = liveKeyLabel(CommandIds.toggleAgentAside);
    return key === "" ? toggleLabel() : `${toggleLabel()} (${key})`;
  };
  onMount(() => {
    toggles.set(card!, () => props.onToggle());
    onCleanup(() => toggles.delete(card!));
  });

  return (
    <article ref={card} class="agent-aside" {...props.attributes}>
      <div class="agent-aside-bar">
        <button
          type="button"
          class="agent-aside-head"
          aria-label={toggleLabel()}
          aria-expanded={!props.collapsed}
          title={toggleTitle()}
          onClick={() => props.onToggle()}
        >
          <span aria-hidden="true">{props.collapsed ? "▸" : "▾"}</span>
          {props.head}
        </button>
        {props.headActions}
      </div>
      <div hidden={props.collapsed}>
        <div class="agent-aside-transcript">
          <For each={props.entries}>
            {(entry) => (
              <TranscriptEntry
                expandedDetails={props.expandedDetails}
                entry={entry}
                keyboardRequestKey={props.keyboardRequestKey}
                latestPromptTurn={null}
                onDetailsToggle={props.onDetailsToggle}
                sectionLabel={null}
                session={props.session}
              />
            )}
          </For>
        </div>
        {props.children}
      </div>
    </article>
  );
}
