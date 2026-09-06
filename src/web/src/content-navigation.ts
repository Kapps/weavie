import type { ClientSession } from "./bridge";
import { parseFileReference } from "./content-links";
import { isSafeAgentLink } from "./editor/preview/markdown-renderer";
import { revealFileIn } from "./files/reveal";
import { clickedLink } from "./navigation";
import { notify } from "./notify/notify";
import { refLinkPrefixFor } from "./terminal/ref-link-store";
import { openUrlExternal } from "./terminal/terminal-links";

/** Shares authored and linkified Markdown navigation, with file links relative to their source document. */
export function installContentNavigation(
  element: HTMLElement,
  session: () => ClientSession | null,
  documentPath: () => string | null,
): () => void {
  const activate = (event: MouseEvent): void => {
    const link = clickedLink(event);
    if (link === undefined) return;
    const { anchor, href } = link;
    event.preventDefault();
    try {
      const target = anchor.getAttribute("data-agent-target") ?? href;
      const kind = anchor.getAttribute("data-agent-kind");
      if (kind === "ref") {
        const owner = session();
        const prefix = owner === null ? null : refLinkPrefixFor(owner);
        if (prefix !== null) openUrlExternal(prefix + target.slice(1));
      } else if (/^(https?:)?\/\//i.test(target) && kind !== "file") {
        openUrlExternal(new URL(target, anchor.baseURI).href);
      } else if (target.startsWith("#")) {
        if (target.length > 1)
          element
            .querySelector(`#${CSS.escape(decodeURIComponent(target.slice(1)))}`)
            ?.scrollIntoView();
      } else if (target.length > 0 && (kind === "file" || isSafeAgentLink(target))) {
        let { path, line } = parseFileReference(target);
        const source = documentPath();
        if (source !== null && !/^(?:[A-Za-z]:|[\\/]|~)/.test(path)) {
          path = source.replace(/[\\/][^\\/]*$/, "/") + decodeURIComponent(path);
        }
        revealFileIn(session(), path, line, true);
      }
    } catch (error: unknown) {
      notify("warn", `Could not open link: ${String(error)}`);
    }
  };
  element.addEventListener("click", activate);
  element.addEventListener("auxclick", activate);
  return () => {
    element.removeEventListener("click", activate);
    element.removeEventListener("auxclick", activate);
  };
}
