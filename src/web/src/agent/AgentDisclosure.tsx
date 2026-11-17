import { ChevronRight } from "lucide-solid";
import type { JSX } from "solid-js";
import { Show } from "solid-js";

/** The transcript's one expander: a native details/summary whose open state lives with its owner. */
export function Disclosure(props: {
  children: JSX.Element;
  class: string;
  summaryClass: string;
  label: JSX.Element;
  open: boolean;
  onToggle: (open: boolean) => void;
}): JSX.Element {
  return (
    <details class={`agent-disclosure ${props.class}`} open={props.open}>
      {/* biome-ignore lint/a11y/noStaticElementInteractions: summary is the native details control. */}
      <summary
        class={props.summaryClass}
        onClick={(event) => {
          event.preventDefault();
          props.onToggle(!props.open);
        }}
      >
        <DisclosureIcon />
        {props.label}
      </summary>
      <Show when={props.open}>{props.children}</Show>
    </details>
  );
}

/** The row every transcript expander shows, whether it opens a body or reveals folded entries. */
export const DISCLOSURE_ROW = "agent-disclosure-row ui-row ui-row-compact";

/** A transcript row that opens a body inset under its label: Thinking and Working. */
export function RowDisclosure(props: {
  children: JSX.Element;
  class: string;
  label: JSX.Element;
  open: boolean;
  onToggle: (open: boolean) => void;
}): JSX.Element {
  return (
    <Disclosure
      class={`agent-row-disclosure ${props.class}`}
      summaryClass={DISCLOSURE_ROW}
      label={props.label}
      open={props.open}
      onToggle={props.onToggle}
    >
      <div class="agent-disclosure-body">{props.children}</div>
    </Disclosure>
  );
}

export function DisclosureIcon(): JSX.Element {
  return (
    <span class="ui-icon agent-disclosure-icon">
      <ChevronRight aria-hidden="true" />
    </span>
  );
}
