import DOMPurify from "dompurify";
import MarkdownIt from "markdown-it";
import type StateCore from "markdown-it/lib/rules_core/state_core.mjs";
import { isFileLineReference } from "../../content-links";
import { sanitizeGithubHtml } from "./github-html";
import { highlightFence } from "./highlight";

export interface MarkdownProfile {
  /** Raw HTML: dropped, kept to GitHub's user-content allowlist, or kept to DOMPurify's broad defaults. */
  html: "none" | "github" | "any";
  /** Images: loaded directly, or left as inert `.agent-image` placeholders the host loads. */
  images: "direct" | "hosted";
  allowMermaid: boolean;
  safeLinksOnly: boolean;
}

/** A profile's configured parser plus the sanitizer every piece of its output must pass through. */
export interface MarkdownEngine {
  markdown: MarkdownIt;
  sanitize(html: string): DocumentFragment;
}

export function createMarkdownRenderer(profile: MarkdownProfile): (content: string) => HTMLElement {
  const { markdown, sanitize } = createMarkdownEngine(profile);
  return (content: string): HTMLElement => {
    const element = document.createElement("div");
    element.append(sanitize(markdown.render(content)));
    return element;
  };
}

export function createMarkdownEngine(profile: MarkdownProfile): MarkdownEngine {
  const markdown = new MarkdownIt({
    html: profile.html !== "none",
    linkify: true,
    typographer: true,
  }).use(taskLists);

  markdown.set({
    highlight: (code, lang) =>
      profile.allowMermaid && lang === "mermaid"
        ? `<pre class="mermaid-pending">${markdown.utils.escapeHtml(code)}</pre>`
        : highlightFence(code, lang),
  });

  if (profile.images === "hosted") {
    markdown.renderer.rules.image = (tokens, index) => {
      const token = tokens[index];
      const escapeHtml = markdown.utils.escapeHtml;
      return `<span class="agent-image" data-src="${escapeHtml(token?.attrGet("src") ?? "")}" data-alt="${escapeHtml(token?.content.trim() ?? "")}"></span>`;
    };
  }

  if (profile.safeLinksOnly) {
    markdown.validateLink = isSafeAgentLink;
    // `hello.ts:42` reads as a `hello.ts:` URI scheme to the link policies here and in DOMPurify, which then
    // drop the href and leave a dead link. Marking it explicitly relative keeps an authored file link whole.
    const normalize = markdown.normalizeLink.bind(markdown);
    markdown.normalizeLink = (url) => normalize(isFileLineReference(url) ? `./${url}` : url);
  }

  return {
    markdown,
    sanitize:
      profile.html === "github"
        ? sanitizeGithubHtml
        : (html: string) => DOMPurify.sanitize(html, { RETURN_DOM_FRAGMENT: true }),
  };
}

// GFM task-list items: a list item whose first paragraph opens with `[ ]` or `[x]` renders a checkbox.
export function taskLists(markdown: MarkdownIt): void {
  markdown.core.ruler.after("inline", "task-lists", renderTaskLists);
}

function renderTaskLists(state: StateCore): void {
  const tokens = state.tokens;
  for (let index = 2; index < tokens.length; index += 1) {
    const item = tokens[index - 2];
    const inline = tokens[index];
    const first = inline?.children?.[0];
    if (
      item?.type !== "list_item_open" ||
      tokens[index - 1]?.type !== "paragraph_open" ||
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
    while (list > 0 && tokens[list]?.level !== item.level - 1) {
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
