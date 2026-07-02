import { type JSX, createEffect, onCleanup, onMount } from "solid-js";
import { openTarget } from "../../bridge";
import { onPreviewThemeChanged } from "../../theme/controller";
import { hydrateMermaid } from "../preview/diagrams";
import { highlightFence } from "../preview/highlight";
// The same hljs token theme the Markdown preview uses, injected into the shadow root so highlighted code is colored.
import HIGHLIGHT_CSS from "../preview/preview-highlight.css?raw";
import { renderNotionMarkdown } from "./notion-markdown";
import { sanitizeSourceHtml } from "./source-html";
import type { SourceDocEntry } from "./source-store";
import { SOURCE_STYLES } from "./source-styles";

// Rich-HTML render of a fetched source doc (Notion) in an OPEN shadow root, overlaying the kept-mounted Monaco host
// like PreviewPane/WebTabPane. CSS is isolated to the shadow tree; the app's theme custom properties pierce the
// boundary, so the content follows dark/light live. Code is syntax-highlighted and mermaid fences are rendered to
// SVG, reusing the Markdown preview's highlightFence/hydrateMermaid. Links are intercepted (http(s) open in the OS
// browser, web tabs deferred). Global shortcuts stay host-owned: this is inline (one document) and the only
// listener here is a capture-phase click, never keydown.
export default function SourceView(props: { doc: () => SourceDocEntry | undefined }): JSX.Element {
  let host!: HTMLDivElement;
  let root: ShadowRoot | undefined;
  // Each render bumps the generation; the async mermaid pass re-checks it after every await so a diagram resolving
  // after a newer render (doc change / theme switch) is dropped instead of landing in stale DOM (mirrors PreviewPane).
  let generation = 0;

  const render = (): void => {
    root ??= host.attachShadow({ mode: "open" });
    generation += 1;
    const gen = generation;
    const style = document.createElement("style");
    style.textContent = SOURCE_STYLES + HIGHLIGHT_CSS;
    const body = document.createElement("div");
    body.className = "wv-source";
    const entry = props.doc();
    if (entry === undefined || entry.status === "loading") {
      body.append(statusNode("loading", "Loading…"));
      root.replaceChildren(style, body);
      return;
    }
    if (entry.status === "error") {
      body.append(statusNode("error", entry.message ?? "Couldn't open that source."));
      root.replaceChildren(style, body);
      return;
    }
    body.append(headerNode(entry.title, entry.editedTime));
    const content = document.createElement("div");
    content.className = "wv-content";
    // A ready doc carries exactly one body: host-rendered `html` (the log viewer, injected as-is) or `markdown`
    // (Notion, rendered here). Neither means a broken source-doc — say so rather than render a blank page.
    const html =
      entry.html ??
      (entry.markdown === undefined ? undefined : renderNotionMarkdown(entry.markdown));
    if (html === undefined) {
      body.append(statusNode("error", "The source arrived without content."));
      root.replaceChildren(style, body);
      return;
    }
    content.innerHTML = sanitizeSourceHtml(html);
    body.append(content);
    highlightCode(content);
    root.replaceChildren(style, body);
    void hydrateMermaid(content, () => gen === generation);
  };

  // Re-render on doc change (loading → ready/error, or a new doc) and on theme switch: mermaid bakes the theme into
  // its SVG so it must re-render (the rest restyles live via the piercing CSS vars, but a re-render is cheap and
  // keeps one path, like PreviewPane).
  createEffect(() => {
    props.doc();
    render();
  });
  onCleanup(onPreviewThemeChanged(render));

  const onClick = (event: MouseEvent): void => {
    // Nodes live inside the shadow tree, so event.target is retargeted to the host — inspect the composed path.
    const path = event.composedPath();
    // A link: preventDefault stops the default <a> navigation tearing down the whole app.
    const anchor = path.find(
      (node): node is HTMLAnchorElement => node instanceof HTMLAnchorElement,
    );
    if (anchor !== undefined) {
      event.preventDefault();
      const href = anchor.getAttribute("href") ?? "";
      if (/^https?:/i.test(href)) {
        // The host resolves it: a Notion link renders natively in another source tab, else it opens as a web tab.
        openTarget(href);
      }
      return;
    }
    // A toggle: drive <details> open/closed ourselves. The embedded WebView doesn't fire the native summary-toggle
    // for a shadow-tree <details>, so we don't rely on its default action (preventDefault avoids a double-toggle).
    const summary = path.find(
      (node): node is HTMLElement => node instanceof HTMLElement && node.tagName === "SUMMARY",
    );
    if (summary?.parentElement instanceof HTMLDetailsElement) {
      event.preventDefault();
      summary.parentElement.open = !summary.parentElement.open;
    }
  };

  onMount(() => {
    host.addEventListener("click", onClick, { capture: true });
    host.focus();
  });
  onCleanup(() => host.removeEventListener("click", onClick, { capture: true }));

  return <div class="editor-source" data-kind="editor" tabindex="0" ref={host} />;
}

// The page header the markdown body omits: the title + last-edited time, above the content. textContent only, so
// the title/time can't inject markup. Properties / path / icon are a later slice.
function headerNode(title: string, editedTime: string): HTMLElement {
  const header = document.createElement("header");
  header.className = "wv-header";
  const h1 = document.createElement("h1");
  h1.className = "wv-title";
  h1.textContent = title.length > 0 ? title : "Untitled";
  header.append(h1);
  const relative = relativeTime(editedTime);
  if (relative.length > 0) {
    const meta = document.createElement("div");
    meta.className = "wv-meta";
    meta.textContent = `Edited ${relative}`;
    header.append(meta);
  }
  return header;
}

// A short "2h ago"-style label from an ISO timestamp, falling back to a local date past a month; "" when absent/bad.
function relativeTime(iso: string): string {
  if (iso.length === 0) {
    return "";
  }
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) {
    return "";
  }
  const minutes = Math.round((Date.now() - then) / 60000);
  if (minutes < 1) {
    return "just now";
  }
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  if (minutes < 60 * 24) {
    return `${Math.round(minutes / 60)}h ago`;
  }
  const days = Math.round(minutes / (60 * 24));
  return days < 30 ? `${days}d ago` : new Date(iso).toLocaleDateString();
}

// A centered loading spinner or error message for the shadow root. The text is set via textContent (never
// innerHTML), so an error reason from the host can't inject markup.
function statusNode(kind: "loading" | "error", text: string): HTMLElement {
  const node = document.createElement("div");
  node.className = kind === "error" ? "wv-status wv-error" : "wv-status";
  if (kind === "loading") {
    const spinner = document.createElement("span");
    spinner.className = "wv-spinner";
    node.append(spinner);
  }
  node.append(document.createTextNode(text));
  return node;
}

// Replaces each `<pre><code class="language-…">` with highlight.js-tokenized markup (the shared highlightFence, same
// as the Markdown preview). An unregistered language returns "" and is left as the plain escaped text.
function highlightCode(body: HTMLElement): void {
  for (const code of body.querySelectorAll<HTMLElement>("pre > code[class*='language-']")) {
    const lang = code.className.match(/language-([\w-]+)/)?.[1] ?? "";
    const highlighted = highlightFence(code.textContent ?? "", lang);
    if (highlighted.length > 0 && code.parentElement !== null) {
      code.parentElement.outerHTML = highlighted;
    }
  }
}
