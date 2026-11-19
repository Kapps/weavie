import type { JSX } from "solid-js";

// The app mark, from the shared `/weavie.png` asset (also backs the favicon and native app icons), sized like an
// icon by `--icon-size`. A full-color brand mark, so it doesn't follow `currentColor`.
export function WeavieIcon(): JSX.Element {
  return (
    <img src="/weavie.png" alt="" aria-hidden="true" class="weavie-icon" style="display:block" />
  );
}
