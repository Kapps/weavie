import DOMPurify from "dompurify";
import MarkdownIt from "markdown-it";
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
  });

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

export function isSafeAgentLink(url: string): boolean {
  if (/^https?:\/\//i.test(url) || /^\.?\.?[\\/]/.test(url) || /^[A-Za-z]:[\\/]/.test(url)) {
    return true;
  }

  return !/^[A-Za-z][A-Za-z\d+.-]*:/.test(url);
}
