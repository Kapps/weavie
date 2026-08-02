import { keyHint } from "../commands/key-hint";
import { dispatchCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { MermaidHydration } from "../editor/preview/diagrams";

const PREVIEW_ICON =
  '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" ' +
  'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M2.062 12.348a1 1 0 0 1 0-.696 ' +
  '10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"/><circle cx="12" cy="12" r="3"/></svg>';
const SOURCE_ICON =
  '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" ' +
  'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m16 18 6-6-6-6"/><path d="m8 6-6 6 6 6"/></svg>';
const toggles = new WeakMap<HTMLElement, () => void>();

function labelToggle(
  button: HTMLButtonElement,
  title: string,
  icon: string,
  previewing: boolean,
): void {
  button.title = title;
  button.setAttribute("aria-pressed", String(previewing));
  button.innerHTML = icon;
}

/** Adds Source/Preview controls to hydrated Mermaid blocks in an agent response. */
export function installAgentMermaid(blocks: readonly MermaidHydration[]): void {
  for (const block of blocks) {
    const holder = document.createElement("div");
    holder.className = "agent-mermaid-block";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "agent-mermaid-toggle";
    const source = block.status === "rendered" ? document.createElement("pre") : block.element;
    source.className = "mermaid-source";
    source.textContent = block.source;

    block.element.replaceWith(holder);
    holder.append(button);
    if (block.status === "rendered") {
      const diagram = block.element;
      button.setAttribute("aria-label", "Toggle Mermaid preview");
      holder.append(diagram, source);
      const title = (previewing: boolean): string =>
        (previewing ? "Show Mermaid source" : "Show Mermaid preview") +
        keyHint(CommandIds.toggleAgentMermaidPreview);
      const refreshLabel = (): void => {
        const previewing = diagram.hidden !== true;
        button.title = title(previewing);
      };
      const showPreview = (previewing: boolean): void => {
        diagram.hidden = !previewing;
        source.hidden = previewing;
        labelToggle(button, title(previewing), previewing ? SOURCE_ICON : PREVIEW_ICON, previewing);
      };
      toggles.set(holder, () => showPreview(diagram.hidden === true));
      button.addEventListener("click", () => {
        void dispatchCommand(CommandIds.toggleAgentMermaidPreview);
      });
      button.addEventListener("mouseenter", refreshLabel);
      button.addEventListener("focus", refreshLabel);
      showPreview(true);
      continue;
    }

    holder.append(source);
    button.setAttribute("aria-disabled", "true");
    const title =
      block.status === "syntax-error"
        ? "Preview unavailable: Mermaid diagram has a syntax error"
        : "Preview unavailable: Mermaid diagram could not be rendered";
    button.setAttribute("aria-label", title);
    labelToggle(button, title, PREVIEW_ICON, false);
  }
}

/** Toggles the focused Mermaid block, or the newest previewable block in the active agent transcript. */
export function toggleActiveAgentMermaid(): boolean {
  const active = document.activeElement;
  const focused =
    active instanceof Element ? active.closest<HTMLElement>(".agent-mermaid-block") : null;
  if (focused !== null) {
    const focusedToggle = toggles.get(focused);
    if (focusedToggle === undefined) {
      return false;
    }
    focusedToggle();
    return true;
  }
  const surface = document.querySelector<HTMLElement>(".agent-surface");
  const latest = Array.from(
    surface?.querySelectorAll<HTMLElement>(".agent-mermaid-block") ?? [],
  ).findLast((block) => toggles.has(block));
  if (latest === undefined) {
    return false;
  }
  const toggle = toggles.get(latest);
  if (toggle === undefined) {
    return false;
  }
  toggle();
  return true;
}
