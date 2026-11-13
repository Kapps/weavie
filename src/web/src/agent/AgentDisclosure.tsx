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

export function DisclosureIcon(): JSX.Element {
  return (
    <span class="ui-icon agent-disclosure-icon">
      <ChevronRight aria-hidden="true" />
    </span>
  );
}
