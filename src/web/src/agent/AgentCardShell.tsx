import type { JSX } from "solid-js";
import { liveKeyHint } from "../commands/keys-live";
import { CommandIds } from "../commands/types";

/** The box a BTW or dashboard card sits in: a head that collapses it and opens it in a tab, then its content. */
export function AgentCardShell(props: {
  label: string;
  collapsed: boolean;
  meta: JSX.Element;
  onToggle: () => void;
  onOpen: () => void;
  onOwn: (event: Event) => void;
  data: Record<`data-${string}`, string>;
  children: JSX.Element;
}): JSX.Element {
  const toggleLabel = () => `${props.collapsed ? "Expand" : "Collapse"} ${props.label}`;
  return (
    <article
      class="agent-aside"
      {...props.data}
      onFocusIn={props.onOwn}
      onPointerDown={props.onOwn}
    >
      <div class="agent-aside-head">
        <button
          type="button"
          class="agent-aside-toggle"
          aria-label={toggleLabel()}
          aria-expanded={!props.collapsed}
          title={`${toggleLabel()}${liveKeyHint(CommandIds.toggleAgentAside)}`}
          onClick={props.onToggle}
        >
          {props.collapsed ? "▸" : "▾"} {props.label}
        </button>
        {props.meta}
        <button
          type="button"
          class="agent-entry-rewind agent-entry-open"
          title={`Open Card in Tab${liveKeyHint(CommandIds.openAgentCard)}`}
          onClick={props.onOpen}
        >
          Open
        </button>
      </div>
      {props.children}
    </article>
  );
}
