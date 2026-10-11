import { For, type JSX, onCleanup } from "solid-js";
import type { ClientSession } from "../bridge";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import { newestVisibleAgentElement } from "./AgentViewport";

const nested = ".agent-aside, .agent-entry-subagent";
const toggles = new Map<HTMLElement, () => void>();

/** Collapses or expands the focused BTW or subagent card, else the newest visible one. */
export function toggleAgentAside(): boolean {
  const active = document.querySelector(".agent-surface.active");
  const focused = document.activeElement?.closest<HTMLElement>(nested);
  const target = focused && active?.contains(focused) ? focused : newestVisibleAgentElement(nested);
  const toggle = target === undefined ? undefined : toggles.get(target);
  if (toggle === undefined) return false;
  toggle();
  return true;
}

/** Lets the toggle command reach one card for as long as it is mounted. */
export function registerNestedToggle(card: HTMLElement, toggle: () => void): void {
  toggles.set(card, toggle);
  onCleanup(() => toggles.delete(card));
}

/** A nested conversation's transcript inside a BTW or subagent card. */
export function NestedTranscript(props: {
  entries: readonly AgentTranscriptEntry[];
  expandedDetails: ReadonlySet<string>;
  keyboardRequestKey: string | null;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  session: ClientSession;
}): JSX.Element {
  return (
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
  );
}
