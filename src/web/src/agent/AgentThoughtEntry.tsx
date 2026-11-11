import type { JSX } from "solid-js";
import type { ClientSession } from "../bridge";
import { agentThoughtDisplay } from "../chrome/agent-default";
import { Disclosure } from "./AgentDisclosure";
import { AgentMarkdown } from "./AgentMarkdown";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export function AgentThoughtEntry(props: {
  entry: AgentTranscriptEntry;
  expandedDetails: ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  session: ClientSession;
}): JSX.Element {
  const display = () => agentThoughtDisplay(props.session.connection.id);
  const key = () => `${props.entry.id}:thought:${display()}`;
  const inline = () => display() === "inline";
  const open = () => inline() !== props.expandedDetails.has(key());
  return (
    <article class="agent-entry agent-entry-thought agent-tone-system">
      <Disclosure
        class="agent-thought"
        label="Thinking"
        open={open()}
        onToggle={(expanded) => props.onDetailsToggle(key(), expanded !== inline())}
      >
        <AgentMarkdown
          cacheKey={props.entry}
          content={props.entry.text ?? ""}
          flavor="agent"
          renderMermaid={!props.entry.streaming}
          session={props.session}
        />
      </Disclosure>
    </article>
  );
}
