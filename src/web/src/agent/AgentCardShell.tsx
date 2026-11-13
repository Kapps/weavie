import type { JSX } from "solid-js";
import { liveKeyHint } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import type { AgentCardRef } from "../editor/tab-entry";
import { AgentCardOpenButton } from "./AgentCardOpenButton";
import { AgentCardToggleContext } from "./agent-row-context";

/** The box a BTW or dashboard card sits in: a head that collapses it and opens it in a tab, then its content. */
export function AgentCardShell(props: {
  label: string;
  collapsed: boolean;
  meta: JSX.Element;
  onToggle: () => void;
  card: AgentCardRef;
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
          class="ui-row agent-aside-toggle"
          aria-label={toggleLabel()}
          aria-expanded={!props.collapsed}
          title={`${toggleLabel()}${liveKeyHint(CommandIds.toggleAgentAside)}`}
          onClick={props.onToggle}
        >
          <span class="agent-aside-toggle-label">
            {props.collapsed ? "▸" : "▾"} {props.label}
          </span>
        </button>
        {props.meta}
        <AgentCardOpenButton card={props.card} />
      </div>
      <AgentCardToggleContext.Provider value={props.onToggle}>
        {props.children}
      </AgentCardToggleContext.Provider>
    </article>
  );
}
