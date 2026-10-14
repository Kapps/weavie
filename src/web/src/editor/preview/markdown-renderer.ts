import DOMPurify from "dompurify";
import MarkdownIt from "markdown-it";
import type StateCore from "markdown-it/lib/rules_core/state_core.mjs";
import { isFileLineReference } from "../../content-links";
import { highlightFence } from "./highlight";

export interface MarkdownProfile {
  allowHtml: boolean;
  allowImages: boolean;
  allowMermaid: boolean;
  safeLinksOnly: boolean;
}

export function createMarkdownRenderer(profile: MarkdownProfile): (content: string) => HTMLElement {
  const markdown = new MarkdownIt({
    html: profile.allowHtml,
    linkify: true,
    typographer: true,
  }).use(taskLists);

  markdown.set({
    highlight: (code, lang) =>
      profile.allowMermaid && lang === "mermaid"
        ? `<pre class="mermaid-pending">${markdown.utils.escapeHtml(code)}</pre>`
        : highlightFence(code, lang),
  });

  if (!profile.allowImages) {
    markdown.renderer.rules.image = (tokens, index) => {
      const alt = tokens[index]?.content.trim();
      return `<span class="markdown-image-blocked">[image${alt ? `: ${markdown.utils.escapeHtml(alt)}` : ""}]</span>`;
    };
  }

  if (profile.safeLinksOnly) {
    markdown.validateLink = isSafeAgentLink;
    // `hello.ts:42` reads as a `hello.ts:` URI scheme to the link policies here and in DOMPurify, which then
    // drop the href and leave a dead link. Marking it explicitly relative keeps an authored file link whole.
    const normalize = markdown.normalizeLink.bind(markdown);
    markdown.normalizeLink = (url) => normalize(isFileLineReference(url) ? `./${url}` : url);
  }

  return (content: string): HTMLElement => {
    const element = document.createElement("div");
    element.innerHTML = DOMPurify.sanitize(markdown.render(content));
    return element;
  };
}

// GFM task-list items: a list item whose first paragraph opens with `[ ]` or `[x]` renders a checkbox.
export function taskLists(markdown: MarkdownIt): void {
  markdown.core.ruler.after("inline", "task-lists", renderTaskLists);
}

function renderTaskLists(state: StateCore): void {
  const tokens = state.tokens;
  for (let index = 2; index < tokens.length; index += 1) {
    const [item, paragraph, inline] = tokens.slice(index - 2, index + 1);
    const first = inline?.children?.[0];
    if (
      item?.type !== "list_item_open" ||
      paragraph?.type !== "paragraph_open" ||
      first?.type !== "text"
    ) {
      continue;
    }
    const marker = /^\[([ xX])\](?=\s|$)/.exec(first.content);
    if (marker === null) {
      continue;
    }
    first.content = first.content.slice(marker[0].length);
    const checkbox = new state.Token("html_inline", "", 0);
    checkbox.content = `<input type="checkbox" class="task-list-item-checkbox" disabled${marker[1] === " " ? "" : " checked"}>`;
    inline?.children?.unshift(checkbox);
    item.attrJoin("class", "task-list-item");
    let list = index - 3;
    while (tokens[list]?.level !== item.level - 1) {
      list -= 1;
    }
    if (!tokens[list]?.attrGet("class")) {
      tokens[list]?.attrSet("class", "contains-task-list");
    }
  }
}

export function isSafeAgentLink(url: string): boolean {
  if (/^https?:\/\//i.test(url) || /^\.?\.?[\\/]/.test(url) || /^[A-Za-z]:[\\/]/.test(url)) {
    return true;
  }

  return !/^[A-Za-z][A-Za-z\d+.-]*:/.test(url);
}
