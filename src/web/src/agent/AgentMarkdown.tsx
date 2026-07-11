import { createEffect, type JSX, onCleanup, onMount } from "solid-js";
import { openTarget, postToHost } from "../bridge";
import { findContentLinks, parseFileReference } from "../content-links";
import { createMarkdownRenderer } from "../editor/preview/markdown-renderer";
import { refLinkPrefix } from "../terminal/ref-link-store";

const renderMarkdown = createMarkdownRenderer({
  allowHtml: false,
  allowImages: false,
  allowMermaid: false,
  safeLinksOnly: true,
});

export function AgentMarkdown(props: { content: string }): JSX.Element {
  let host: HTMLDivElement | undefined;

  createEffect(() => {
    const rendered = renderMarkdown(props.content);
    linkifyText(rendered, refLinkPrefix() !== null);
    host?.replaceChildren(...rendered.childNodes);
  });

  onMount(() => {
    const activateLink = (event: MouseEvent): void => {
      const anchor = event.target instanceof Element ? event.target.closest("a") : null;
      if (anchor instanceof HTMLAnchorElement) {
        event.preventDefault();
        activate(anchor);
      }
    };
    host?.addEventListener("click", activateLink);
    onCleanup(() => host?.removeEventListener("click", activateLink));
  });

  return <div class="agent-markdown" ref={host} />;
}

function activate(anchor: HTMLAnchorElement): void {
  const target = anchor.dataset.agentTarget ?? anchor.getAttribute("href") ?? "";
  if (anchor.dataset.agentKind === "ref") {
    const prefix = refLinkPrefix();
    if (prefix !== null) {
      openTarget(prefix + target.slice(1));
    }
    return;
  }

  if (/^https?:\/\//i.test(target)) {
    openTarget(target);
    return;
  }

  if (target.length > 0 && !target.startsWith("#") && !hasScheme(target)) {
    const { path, line } = parseFileReference(target);
    postToHost({ type: "reveal-file", path, line, preview: true });
  }
}

function linkifyText(root: HTMLElement, includeRefs: boolean): void {
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes: Text[] = [];
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    if (node instanceof Text && !node.parentElement?.closest("a, code, pre")) {
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

function hasScheme(value: string): boolean {
  return /^[A-Za-z][A-Za-z\d+.-]*:/.test(value) && !/^[A-Za-z]:[\\/]/.test(value);
}
