import type { JSX } from "solid-js";

// The app mark, from the shared `/weavie.svg` asset (also backs the favicon and the .ico/appiconset). Sized
// via `size` (number = px), defaulting to 1em. A full-color brand mark, so it doesn't follow `currentColor`.
export function WeavieIcon(props: { size?: number | string }): JSX.Element {
  const size = (): string =>
    props.size === undefined
      ? "1em"
      : typeof props.size === "number"
        ? `${props.size}px`
        : props.size;
  return (
    <img
      src="/weavie.svg"
      alt=""
      aria-hidden="true"
      // Style as a string, not an object: a dynamic object-style emits a `setStyleProperty` helper that
      // solid-js@1.9.3 doesn't export, breaking the production build.
      style={`width:${size()};height:${size()};display:block`}
    />
  );
}
