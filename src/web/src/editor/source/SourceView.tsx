import { type JSX, createEffect, onCleanup, onMount } from "solid-js";
import { openTarget } from "../../bridge";
import { onPreviewThemeChanged } from "../../theme/controller";
import { hydrateMermaid } from "../preview/diagrams";
import { highlightFence } from "../preview/highlight";
// The same hljs token theme the Markdown preview uses, injected into the shadow root so highlighted code is colored.
import HIGHLIGHT_CSS from "../preview/preview-highlight.css?raw";
import { sanitizeSourceHtml } from "./source-html";
import { SOURCE_STYLES } from "./source-styles";

// Rich-HTML render of a fetched source doc (Notion) in an OPEN shadow root, overlaying the kept-mounted Monaco host
// like PreviewPane/WebTabPane. CSS is isolated to the shadow tree; the app's theme custom properties pierce the
// boundary, so the content follows dark/light live. Code is syntax-highlighted and mermaid fences are rendered to
// SVG, reusing the Markdown preview's highlightFence/hydrateMermaid. Links are intercepted (http(s) open in the OS
// browser, web tabs deferred). Global shortcuts stay host-owned: this is inline (one document) and the only
// listener here is a capture-phase click, never keydown.
export default function SourceView(props: { html: () => string }): JSX.Element {
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
    body.innerHTML = sanitizeSourceHtml(props.html());
    highlightCode(body);
    root.replaceChildren(style, body);
    void hydrateMermaid(body, () => gen === generation);
  };

  // Re-render on doc change and on theme switch: mermaid bakes the theme into its SVG so it must re-render (the rest
  // restyles live via the piercing CSS vars, but a re-render is cheap and keeps one path, like PreviewPane).
  createEffect(() => {
    props.html();
    render();
  });
  onCleanup(onPreviewThemeChanged(render));

  const onClick = (event: MouseEvent): void => {
    // The anchor lives inside the shadow tree, so event.target is retargeted to the host — find it on the composed
    // path instead. Without this the default <a> navigation fires and tears down the whole app.
    const anchor = event
      .composedPath()
      .find((node): node is HTMLAnchorElement => node instanceof HTMLAnchorElement);
    if (anchor === undefined) {
      return;
    }
    event.preventDefault();
    const href = anchor.getAttribute("href") ?? "";
    if (/^https?:/i.test(href)) {
      // The host resolves it: a Notion link renders natively in another source tab, anything else opens as a web tab.
      openTarget(href);
    }
  };

  onMount(() => {
    host.addEventListener("click", onClick, { capture: true });
    host.focus();
  });
  onCleanup(() => host.removeEventListener("click", onClick, { capture: true }));

  return <div class="editor-source" data-kind="editor" tabindex="0" ref={host} />;
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
