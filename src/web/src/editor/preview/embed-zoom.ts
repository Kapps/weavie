// Zoomable preview embeds: a hover magnifier on every image / Mermaid diagram that opens it in the
// full-app EmbedLightbox. Shared by the Markdown preview (light DOM) and SourceView (shadow root); the
// buttons are built with plain DOM because both bodies are sanitized raw HTML, not JSX.

import { createSignal } from "solid-js";
import { formatKey } from "../../commands/keybindings";
import { findCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { hydrateMermaid } from "./diagrams";

/** The open lightbox: every zoomable embed in the active view plus the one being shown. */
export interface EmbedZoomState {
  targets: HTMLElement[];
  index: number;
}

const [zoomedEmbed, setZoomedEmbed] = createSignal<EmbedZoomState | null>(null);

/** The open lightbox state (null when closed); App gates EmbedLightbox on it. */
export { zoomedEmbed };

/** Closes the lightbox. */
export function closeEmbedZoom(): void {
  setZoomedEmbed(null);
}

/** Steps the open lightbox to an adjacent embed, wrapping at the ends; no-op while closed. */
export function stepEmbedZoom(delta: number): void {
  setZoomedEmbed((state) => {
    if (state === null) {
      return null;
    }
    const count = state.targets.length;
    return { ...state, index: (state.index + delta + count) % count };
  });
}

// Embeds worth zooming: images and rendered Mermaid diagrams.
const zoomables = (root: ParentNode): HTMLElement[] => [
  ...root.querySelectorAll<HTMLElement>("img, .mermaid-rendered"),
];

const MAGNIFIER_SVG =
  '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" ' +
  'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
  '<circle cx="11" cy="11" r="8" /><line x1="21" y1="21" x2="16.5" y2="16.5" />' +
  '<line x1="11" y1="8" x2="11" y2="14" /><line x1="8" y1="11" x2="14" y2="11" /></svg>';

/**
 * Dresses the embeds under a freshly rendered preview body with the zoom magnifier and hydrates its
 * Mermaid fences, dressing again once the diagram wrappers exist. `isCurrent` drops a hydration that
 * resolves after a newer render.
 */
export function installEmbedZoomAndMermaid(root: HTMLElement, isCurrent: () => boolean): void {
  installEmbedZoom(root);
  void hydrateMermaid(root, isCurrent).then(() => {
    if (isCurrent()) {
      installEmbedZoom(root);
    }
  });
}

// Adds the hover magnifier to every embed under `root` that doesn't have one yet. Called per render
// stage (images land synchronously, Mermaid diagrams hydrate later), so it skips already-dressed embeds.
function installEmbedZoom(root: ParentNode): void {
  for (const target of zoomables(root)) {
    if (target.closest(".embed-zoom") !== null) {
      continue;
    }
    // A Mermaid wrapper is already a block container the button can pin to; a bare <img> needs one.
    let holder = target;
    if (!target.classList.contains("mermaid-rendered")) {
      holder = document.createElement("span");
      target.replaceWith(holder);
      holder.append(target);
    }
    holder.classList.add("embed-zoom");
    holder.append(zoomButton(root, target));
  }
}

// The magnifier button; its tooltip advertises the Zoom Embed keybinding from the live catalog.
function zoomButton(root: ParentNode, target: HTMLElement): HTMLButtonElement {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "embed-zoom-btn";
  button.innerHTML = MAGNIFIER_SVG;
  const key = findCommand(CommandIds.zoomEmbed)?.keys[0];
  button.title = key === undefined ? "Zoom" : `Zoom (${formatKey(key)})`;
  button.setAttribute("aria-label", "Zoom");
  button.addEventListener("click", () => {
    // Re-collect at click time so diagrams hydrated after this button was installed are in the set.
    const targets = zoomables(root);
    setZoomedEmbed({ targets, index: targets.indexOf(target) });
  });
  return button;
}

/**
 * The Zoom Embed command: opens the lightbox on the active preview's first embed, or advances an open
 * lightbox to the next one. Declines (false) when no view with an embed is showing, so the keybinding
 * falls through (in the Monaco editor the chord is redo on some platforms).
 */
export function zoomActiveEmbed(): boolean {
  if (zoomedEmbed() !== null) {
    stepEmbedZoom(1);
    return true;
  }
  const root =
    document.querySelector(".editor-preview-body") ??
    document.querySelector(".editor-source")?.shadowRoot ??
    null;
  const targets = root === null ? [] : zoomables(root);
  if (targets.length === 0) {
    return false;
  }
  setZoomedEmbed({ targets, index: 0 });
  return true;
}
