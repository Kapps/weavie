import { createEffect, type JSX, onCleanup, onMount } from "solid-js";
import type { ClientSession } from "../bridge";
import { type ContentLinkKind, findContentLinks, parseFileReference } from "../content-links";
import { hydrateMermaid } from "../editor/preview/diagrams";
import { createMarkdownRenderer } from "../editor/preview/markdown-renderer";
import { refLinkPrefixFor } from "../terminal/ref-link-store";
import { openUrlExternal } from "../terminal/terminal-links";
import { onPreviewThemeChanged } from "../theme/controller";
import { installAgentMermaid } from "./agent-mermaid";

const renderMarkdown = createMarkdownRenderer({
  allowHtml: false,
  allowImages: false,
  allowMermaid: true,
  safeLinksOnly: true,
});

interface CachedMarkdown {
  content: string;
  includeRefs: boolean;
  template: HTMLElement;
}

const renderedMarkdown = new WeakMap<object, CachedMarkdown>();

export function AgentMarkdown(props: {
  cacheKey: object;
  content: string;
  renderMermaid: boolean;
  session: ClientSession | null;
}): JSX.Element {
  let host: HTMLDivElement | undefined;
  let generation = 0;
  let unsubscribeTheme: (() => void) | undefined;

  const render = (): void => {
    generation += 1;
    const currentGeneration = generation;
    const shouldHydrate = props.renderMermaid;
    const includeRefs = props.session !== null && refLinkPrefixFor(props.session) !== null;
    const rendered = renderCached(props.cacheKey, props.content, includeRefs);
    const hasMermaid = rendered.querySelector("pre.mermaid-pending") !== null;
    if (hasMermaid && shouldHydrate && unsubscribeTheme === undefined) {
      unsubscribeTheme = onPreviewThemeChanged(render);
    } else if ((!hasMermaid || !shouldHydrate) && unsubscribeTheme !== undefined) {
      unsubscribeTheme();
      unsubscribeTheme = undefined;
    }
    host?.replaceChildren(...rendered.childNodes);
    if (host !== undefined && hasMermaid && shouldHydrate) {
      void hydrateMermaid(host, () => currentGeneration === generation).then((blocks) => {
        if (currentGeneration === generation) {
          installAgentMermaid(blocks);
        }
      });
    }
  };

  createEffect(render);
  onCleanup(() => {
    generation += 1;
    unsubscribeTheme?.();
  });

  onMount(() => {
    const activateLink = (event: MouseEvent): void => {
      const anchor = event.target instanceof Element ? event.target.closest("a") : null;
      if (anchor instanceof HTMLAnchorElement) {
        event.preventDefault();
        activate(anchor, props.session);
      }
    };
    host?.addEventListener("click", activateLink);
    onCleanup(() => host?.removeEventListener("click", activateLink));
  });

  return <div class="agent-markdown" ref={host} />;
}

function renderCached(cacheKey: object, content: string, includeRefs: boolean): HTMLElement {
  let cached = renderedMarkdown.get(cacheKey);
  if (cached === undefined || cached.content !== content || cached.includeRefs !== includeRefs) {
    const template = renderMarkdown(content);
    linkifyText(template, includeRefs);
    cached = { content, includeRefs, template };
    renderedMarkdown.set(cacheKey, cached);
  }
  return cached.template.cloneNode(true) as HTMLElement;
}

function activate(anchor: HTMLAnchorElement, session: ClientSession | null): void {
  const target = anchor.dataset.agentTarget ?? anchor.getAttribute("href") ?? "";
  // A linkified span carries the kind the shared grammar already decided; re-deriving it from the text here
  // would misread `processor.go:1654` as a `processor.go:` URI scheme. Only an authored href needs classifying.
  switch (anchor.dataset.agentKind ?? classifyHref(target)) {
    case "ref": {
      const prefix = session === null ? null : refLinkPrefixFor(session);
      if (prefix !== null) {
        openUrlExternal(prefix + target.slice(1));
      }
      return;
    }
    case "url":
      openUrlExternal(target);
      return;
    case "file": {
      const { path, line } = parseFileReference(target);
      session?.feature("files").publish("reveal", { path, line, preview: true });
      return;
    }
    default:
      return;
  }
}

/** Classifies a link the markdown author wrote (as opposed to one linkify found), or null for an in-page jump. */
function classifyHref(href: string): ContentLinkKind | null {
  if (/^https?:\/\//i.test(href)) {
    return "url";
  }
  // isSafeAgentLink already rejected every other scheme before this href reached the DOM, so what is left
  // is a workspace path.
  return href.length > 0 && !href.startsWith("#") ? "file" : null;
}

function linkifyText(root: HTMLElement, includeRefs: boolean): void {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes: Text[] = [];
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    // Linkify inline `code` spans (the idiomatic way to quote a path), but leave fenced `pre` blocks
    // literal — their highlight spans split paths across text nodes and code samples shouldn't auto-link.
    if (node instanceof Text && !node.parentElement?.closest("a, pre")) {
      nodes.push(node);
    }
  }

  for (const node of nodes) {
    const matches = findContentLinks(node.data, includeRefs);
    if (matches.length === 0) {
      continue;
    }

    const fragment = document.createDocumentFragment();
    let cursor = 0;
    for (const match of matches) {
      fragment.append(node.data.slice(cursor, match.start));
      const anchor = document.createElement("a");
      anchor.href = match.kind === "url" ? match.text : "#";
      anchor.dataset.agentKind = match.kind;
      anchor.dataset.agentTarget = match.text;
      anchor.textContent = match.text;
      fragment.append(anchor);
      cursor = match.end;
    }
    fragment.append(node.data.slice(cursor));
    node.replaceWith(fragment);
  }
}
