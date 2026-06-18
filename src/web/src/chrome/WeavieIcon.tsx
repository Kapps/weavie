import type { JSX } from "solid-js";

// The app mark — the full-color weavie icon (red/green/blue source chips weaving into the editor on a
// charcoal tile). Loaded from the shared `/weavie.svg` asset, which is the single source of truth: the
// same file backs the favicon and is rasterized into the Windows .ico / macOS appiconset. Sized via the
// `size` prop (number = px), defaulting to 1em so it tracks the container's font-size (the title bar uses
// 1em; the welcome screen sizes it up via font-size). It's a full-color brand mark now, so unlike the old
// weave stroke it no longer follows `currentColor`.
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
      // `setStyleProperty` helper that solid-js@1.9.3 doesn't export (vite-plugin-solid version skew),
      // which breaks the production build. The string form compiles to the exported `style` helper.
      style={`width:${size()};height:${size()};display:block`}
    />
  );
}
