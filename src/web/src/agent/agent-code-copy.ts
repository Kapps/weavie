import { writeClipboardExact } from "../clipboard";
import { keyHint } from "../commands/key-hint";
import { CommandIds } from "../commands/types";

const COPY_ICON =
  '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" ' +
  'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect width="14" height="14" x="8" y="8" ' +
  'rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg>';
const COPIED_ICON =
  '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" ' +
  'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5"/></svg>';
const COPIED_MS = 1500;
const resets = new WeakMap<HTMLButtonElement, number>();

/** Wraps each fenced code block in rendered agent markdown with a copy button. */
export function decorateAgentCodeBlocks(root: HTMLElement): void {
  for (const pre of root.querySelectorAll<HTMLElement>("pre:not(.mermaid-pending)")) {
    const holder = document.createElement("div");
    holder.className = "agent-code-block";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "agent-code-copy";
    button.setAttribute("aria-label", "Copy code");
    button.innerHTML = COPY_ICON;
    pre.replaceWith(holder);
    holder.append(button, pre);
  }
}

/** Delegates copy-button clicks and live shortcut titles for one rendered markdown host. */
export function installAgentCodeCopy(host: HTMLElement): () => void {
  const buttonOf = (event: Event): HTMLButtonElement | null =>
    event.target instanceof Element
      ? event.target.closest<HTMLButtonElement>(".agent-code-copy")
      : null;
  const click = (event: MouseEvent): void => {
    const block = buttonOf(event)?.closest<HTMLElement>(".agent-code-block");
    if (block != null) {
      copyBlock(block);
    }
  };
  const refreshTitle = (event: Event): void => {
    const button = buttonOf(event);
    if (button !== null && !resets.has(button)) {
      button.title = copyTitle();
    }
  };
  host.addEventListener("click", click);
  host.addEventListener("pointerover", refreshTitle);
  host.addEventListener("focusin", refreshTitle);
  return () => {
    host.removeEventListener("click", click);
    host.removeEventListener("pointerover", refreshTitle);
    host.removeEventListener("focusin", refreshTitle);
  };
}

/** Copies the focused code block, or the newest one in the active agent transcript. */
export function copyActiveAgentCodeBlock(): boolean {
  const active = document.activeElement;
  const block =
    (active instanceof Element ? active.closest<HTMLElement>(".agent-code-block") : null) ??
    Array.from(
      document
        .querySelector(".agent-surface")
        ?.querySelectorAll<HTMLElement>(".agent-code-block") ?? [],
    ).at(-1);
  return block !== undefined && copyBlock(block);
}

function copyTitle(): string {
  return `Copy code${keyHint(CommandIds.copyAgentCodeBlock)}`;
}

function copyBlock(block: HTMLElement): boolean {
  const pre = block.querySelector("pre");
  const button = block.querySelector<HTMLButtonElement>(".agent-code-copy");
  if (pre === null || button === null) {
    return false;
  }
  writeClipboardExact((pre.textContent ?? "").replace(/\n$/, ""));
  window.clearTimeout(resets.get(button));
  button.innerHTML = COPIED_ICON;
  button.title = "Copied";
  button.classList.add("copied");
  resets.set(
    button,
    window.setTimeout(() => {
      resets.delete(button);
      button.innerHTML = COPY_ICON;
      button.title = copyTitle();
      button.classList.remove("copied");
    }, COPIED_MS),
  );
  return true;
}
