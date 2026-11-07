// The edit box a preview block opens into: the source at the block's own size, and a footer that finishes the
// edit and, for a block the agent changed, shows the agent's original inside the same box.

import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import { removedText, tooManyChangesNote } from "./changes/change-marks";
import { wordDiff } from "./changes/word-diff";

export const HINT = "wv-preview-block-hint";
export const SHOW_ORIGINAL = "wv-preview-block-show-original";
const ORIGINAL = "wv-preview-block-original";
const PROSE_BLOCK = "p, h1, h2, h3, h4, h5, h6, pre, blockquote, table, hr, .mermaid-rendered";

export function buildEditBox(
  unit: HTMLElement,
  source: string,
  code: boolean,
  original: string | undefined,
): { box: HTMLElement; textarea: HTMLTextAreaElement } {
  const textarea = document.createElement("textarea");
  textarea.className = "wv-preview-block-input";
  textarea.rows = 1;
  textarea.value = source;
  const box = document.createElement("div");
  box.className = "wv-preview-block-box";
  box.dataset.value = source;
  box.toggleAttribute("data-code", code);
  const hint = document.createElement("div");
  hint.className = HINT;
  if (original !== undefined) {
    const changed = document.createElement("span");
    changed.textContent = "Changed by agent";
    hint.append(
      changed,
      footerButton(SHOW_ORIGINAL, "Show original", CommandIds.previewShowOriginal),
    );
  }
  hint.append(footerButton("", "Done", CommandIds.previewFinishBlock));
  matchRendered(box, unit);
  box.append(textarea, hint);
  return { box, textarea };
}

/** Shows or hides the agent's original under the source, with its removed words marked. */
export function toggleOriginal(box: HTMLElement, current: string, original: string): void {
  const shown = box.querySelector(`.${ORIGINAL}`);
  const button = box.querySelector(`.${SHOW_ORIGINAL}`);
  if (button !== null) button.textContent = shown === null ? "Hide original" : "Show original";
  if (shown !== null) {
    shown.remove();
    return;
  }
  const view = document.createElement("div");
  view.className = ORIGINAL;
  const diff = wordDiff(current, original);
  const removed = diff?.inserted ?? [];
  let at = 0;
  for (const { start, end } of removed) {
    view.append(original.slice(at, start), removedText(original.slice(start, end)));
    at = end;
  }
  view.append(original.slice(at));
  if (diff === null) view.append(" ", tooManyChangesNote());
  box.querySelector(`.${HINT}`)?.before(view);
}

function footerButton(className: string, label: string, command: string): HTMLButtonElement {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `wv-preview-block-footer-action ${className}`.trim();
  button.textContent = `${label}${keyHint(command)}`;
  button.title = button.textContent;
  return button;
}

// The source sits where the rendered block did, at its size, so opening it barely moves the page.
function matchRendered(box: HTMLElement, unit: HTMLElement): void {
  const first = unit.firstElementChild;
  const style = getComputedStyle(first?.matches(PROSE_BLOCK) ? first : unit);
  box.style.marginBlock = `${style.marginTop} ${style.marginBottom}`;
  if (box.hasAttribute("data-code")) return;
  box.style.fontSize = style.fontSize;
  box.style.fontWeight = style.fontWeight;
  box.style.lineHeight = style.lineHeight;
}
