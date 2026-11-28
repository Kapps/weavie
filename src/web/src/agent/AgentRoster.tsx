import { type Accessor, createEffect, createSignal, type JSX, on, onCleanup } from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyHint } from "../commands/keys-live";
import { registerCommand, runCommandWithFeedback } from "../commands/registry";
import { DisclosureIcon } from "./AgentDisclosure";
import type { AgentPaneModel } from "./pane-store";
import "./agent-roster.css";

/**
 * A list docked above the composer that folds to its header: the fold state, the command that toggles it while the
 * list has rows, and the header itself. Each transcript generation starts as `startsExpanded` says.
 */
export function createRoster(options: {
  model: AgentPaneModel;
  label: string;
  command: string;
  /** The context key that is true while the list has rows. */
  available: string;
  count: Accessor<number>;
  startsExpanded: boolean;
}): { expanded: Accessor<boolean>; Header: (props: { children: JSX.Element }) => JSX.Element } {
  const [expanded, setExpanded] = createSignal(options.startsExpanded);
  let toggle: HTMLButtonElement | undefined;
  createEffect(on(options.model.generation, () => setExpanded(options.startsExpanded)));
  createEffect(() => setContext(options.available, options.count() > 0));
  onCleanup(() => setContext(options.available, false));
  onCleanup(
    registerCommand(options.command, (_args, context) => {
      if (context.session !== options.model.session || options.count() === 0) return false;
      setExpanded((value) => !value);
      if (expanded()) toggle?.focus({ preventScroll: true });
      return true;
    }),
  );
  return {
    expanded,
    Header: (props) => (
      <button
        ref={toggle}
        type="button"
        class="ui-row agent-roster-toggle"
        aria-expanded={expanded()}
        aria-label={`${expanded() ? "Collapse" : "Expand"} ${options.label}`}
        title={`Expand or Collapse ${options.label}${liveKeyHint(options.command)}`}
        onClick={() => void runCommandWithFeedback(options.command)}
      >
        <DisclosureIcon />
        <span>{options.label}</span>
        <span>{options.count()}</span>
        {props.children}
      </button>
    ),
  };
}
