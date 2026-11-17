import type { JSX } from "solid-js";

/** Content that folds to no height, and leaves focus and the accessibility tree, while collapsed. */
export function CollapsibleCardContent(props: {
  collapsed: boolean;
  children: JSX.Element;
}): JSX.Element {
  return (
    <div
      class="agent-card-content"
      classList={{ collapsed: props.collapsed }}
      inert={props.collapsed}
      aria-hidden={props.collapsed}
    >
      <div>{props.children}</div>
    </div>
  );
}
