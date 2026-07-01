import type { JSX } from "solid-js";

// A monochrome Notion mark (an angular "N" in a rounded page), stroke-styled to match the lucide tab icons. Used
// for source tabs — Notion is the only source today; when a second lands, the tab should pick the icon by source id.
export function NotionIcon(props: { size?: number; class?: string }): JSX.Element {
  const size = props.size ?? 13;
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
      class={props.class}
      aria-hidden="true"
    >
      <rect x="3.5" y="3.5" width="17" height="17" rx="3" />
      <path d="M9 16.5V8l6 8V8" />
    </svg>
  );
}
