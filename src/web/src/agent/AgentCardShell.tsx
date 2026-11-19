import type { JSX } from "solid-js";
import { DisclosureIcon } from "./AgentDisclosure";
import { AgentCardToggleContext } from "./agent-row-context";
import { CollapsibleCardContent } from "./CollapsibleCardContent";

/** The box a BTW, dashboard or inline reply card sits in: a head that collapses it and holds its actions, then its body. */
export function AgentCardShell(props: {
  label: string;
  collapsed: boolean;
  /** Whether the card shows its head alone, with no divider or body under it. */
  headOnly: boolean;
  meta: JSX.Element;
  onToggle: () => void;
  /** The shortcut that toggles this card, as a tooltip suffix; empty when none reaches it. */
  toggleHint: string;
  /** What the head offers after its meta: Open in a tab, Dismiss. */
  actions: JSX.Element;
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
          title={`${toggleLabel()}${props.toggleHint}`}
          onClick={props.onToggle}
        >
          <DisclosureIcon />
          <span class="agent-aside-toggle-label">{props.label}</span>
        </button>
        {props.meta}
        {props.actions}
      </div>
      <CollapsibleCardContent collapsed={props.headOnly}>
        <div class="agent-aside-body">
          <AgentCardToggleContext.Provider value={props.onToggle}>
            {props.children}
          </AgentCardToggleContext.Provider>
        </div>
      </CollapsibleCardContent>
    </article>
  );
}
