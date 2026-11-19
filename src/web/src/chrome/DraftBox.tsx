import type { JSX } from "solid-js";

/** A multi-line field: the box holds the block padding, so a scrolled draft is clipped inside it. */
export function DraftBox(props: { class: string; children: JSX.Element }): JSX.Element {
  return (
    // biome-ignore lint/a11y/noStaticElementInteractions: A press on the box's padding lands in its textarea, which stays keyboard-focusable.
    <div
      class={`ui-draft ${props.class}`}
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          event.preventDefault();
          event.currentTarget.querySelector("textarea")?.focus();
        }
      }}
    >
      {props.children}
    </div>
  );
}
