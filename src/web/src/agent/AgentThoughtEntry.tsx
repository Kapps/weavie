import { ChevronRight } from "lucide-solid";
import { createMemo, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { Disclosure } from "./AgentDisclosure";
import { AgentMarkdown } from "./AgentMarkdown";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { thoughtPreview } from "./AgentThoughtPreview";
import { ForEachId } from "./ForEachId";

export function AgentThoughtEntry(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  session: ClientSession;
}): JSX.Element {
  const history = () => props.entry.kind === "thought-history";
  const preview = createMemo(() =>
    props.entry.thoughtCollapsed ? thoughtPreview(props.entry.text ?? "") : "",
  );
  const text = () => <ThoughtText entry={props.entry} session={props.session} />;
  return (
    <article class={`agent-entry agent-entry-${props.entry.kind} agent-tone-system`}>
      <Show when={history() || props.entry.thoughtCollapsed} fallback={text()}>
        <Disclosure
          class="agent-thought"
          label={
            <span class="agent-thought-row ui-row ui-row-compact">
              <span class="ui-icon">
                <ChevronRight aria-hidden="true" />
              </span>
              <span class="agent-thought-label">
                {history() ? `Earlier thinking · ${props.entry.thoughts!.length}` : "Thinking"}
              </span>
              <Show when={!history()}>
                <span class="agent-thought-preview">{preview()}</span>
              </Show>
            </span>
          }
          open={props.expandedDetails.has(props.entry.id)}
          onToggle={(open) => props.onDetailsToggle(props.entry.id, open)}
        >
          <div class="agent-thought-body">
            <Show when={history()} fallback={text()}>
              <div class="agent-thought-history">
                <ForEachId each={props.entry.thoughts!}>
                  {(entry) => <ThoughtText entry={entry()} session={props.session} />}
                </ForEachId>
              </div>
            </Show>
          </div>
        </Disclosure>
      </Show>
    </article>
  );
}

function ThoughtText(props: { entry: AgentTranscriptEntry; session: ClientSession }): JSX.Element {
  return (
    <AgentMarkdown
      cacheKey={props.entry}
      content={props.entry.text ?? ""}
      flavor="agent"
      renderMermaid={!props.entry.streaming}
      session={props.session}
    />
  );
}
