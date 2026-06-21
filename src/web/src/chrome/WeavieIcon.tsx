import type { JSX } from "solid-js";

// The app mark, loaded from the shared `/weavie.svg` asset (the single source of truth that also backs the
// favicon and the Windows .ico / macOS appiconset). Sized via the `size` prop (number = px), defaulting to
// 1em so it tracks the container's font-size. A full-color brand mark, so it doesn't follow `currentColor`.
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
      // Style as a string, not an object: a dynamic object-style makes the Solid compiler emit a
      // `setStyleProperty` helper that solid-js@1.9.3 doesn't export, breaking the production build. The
      // string form compiles to the exported `style` helper.
      style={`width:${size()};height:${size()};display:block`}
    />
  );
}
