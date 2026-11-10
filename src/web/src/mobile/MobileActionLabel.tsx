import type { JSX } from "solid-js";

/** An action button's content: its label on wide layouts, its icon on touch chrome. */
export function MobileActionLabel(props: {
  icon: JSX.Element;
  children: JSX.Element;
}): JSX.Element {
  return (
    <>
      <span class="mobile-action-wide">{props.children}</span>
      <span class="mobile-action-compact ui-icon" aria-hidden="true">
        {props.icon}
      </span>
    </>
  );
}
