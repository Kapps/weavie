import { createMemo, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { Disclosure } from "./AgentDisclosure";
import { AgentMarkdown } from "./AgentMarkdown";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { thoughtPreview } from "./AgentThoughtPreview";

export function AgentThoughtEntry(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  session: ClientSession;
}): JSX.Element {
  const preview = createMemo(() =>
    props.entry.thoughtCollapsed ? thoughtPreview(props.entry.text ?? "") : "",
  );
  const text = () => <ThoughtText entry={props.entry} session={props.session} />;
  return (
    <article class={`agent-entry agent-entry-${props.entry.kind} agent-tone-system`}>
      <Show when={props.entry.thoughtCollapsed} fallback={text()}>
        <Disclosure
          class="agent-thought"
          label={
            <span class="agent-thought-row ui-row ui-row-compact">
              <span class="agent-thought-label">Thinking</span>
              <span class="agent-thought-preview">{preview()}</span>
            </span>
          }
          open={props.expandedDetails.has(props.entry.id)}
          onToggle={(open) => props.onDetailsToggle(props.entry.id, open)}
        >
          <div class="agent-thought-body">{text()}</div>
        </Disclosure>
      </Show>
    </article>
  );
}

function ThoughtText(props: { entry: AgentTranscriptEntry; session: ClientSession }): JSX.Element {
  return (
    <AgentMarkdown
      attachable={false}
      cacheKey={props.entry}
      content={props.entry.text ?? ""}
      flavor="agent"
      renderMermaid={!props.entry.streaming}
      session={props.session}
    />
  );
}
