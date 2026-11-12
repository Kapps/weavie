import type { JSX } from "solid-js";

/** A leading icon inside a composer control, with pointer input left to the control itself. */
export function NewSessionControl(props: {
  icon: JSX.Element;
  children: JSX.Element;
}): JSX.Element {
  return (
    <div class="session-composer-control">
      {props.children}
      <span class="session-composer-control-icon ui-icon" aria-hidden="true">
        {props.icon}
      </span>
    </div>
  );
}
