import type { JSX } from "solid-js";

// Placeholder app logo: two interlaced strokes (a "weave") in the current color — our own brand mark, not
// a library icon (it'll be replaced with the real logo). Inline SVG so it inherits `currentColor`, themes
// for free, and ships no asset request. Defaults to 1em so its size follows the container's font-size.
export function WeavieIcon(props: { size?: number | string }): JSX.Element {
  const size = (): string =>
    props.size === undefined
      ? "1em"
      : typeof props.size === "number"
        ? `${props.size}px`
        : props.size;
  return (
    <svg width={size()} height={size()} viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <path
        d="M2 5c5 0 5 14 10 14s5-14 10-14"
        stroke="currentColor"
        stroke-width="2.6"
        stroke-linecap="round"
      />
      <path
        d="M2 19c5 0 5-14 10-14s5 14 10 14"
        stroke="currentColor"
        stroke-width="2.6"
        stroke-linecap="round"
        opacity="0.5"
      />
    </svg>
  );
}
