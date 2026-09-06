import { notify } from "./notify/notify";

export function clickedLink(event: MouseEvent): { anchor: Element; href: string } | undefined {
  if (event.defaultPrevented || (event.button !== 0 && event.button !== 1)) return;
  const anchor = event
    .composedPath()
    .find((node): node is Element => node instanceof Element && node.localName === "a");
  const href = anchor?.getAttribute("href") ?? anchor?.getAttribute("xlink:href");
  return anchor !== undefined && href != null ? { anchor, href } : undefined;
}

/** Owns otherwise-unhandled anchor navigation; feature-specific links get first refusal. */
export function installNavigationGuard(
  document: Document,
  open: (url: string) => void,
): () => void {
  const navigate = (event: MouseEvent): void => {
    const link = clickedLink(event);
    if (link === undefined) return;
    const { anchor, href } = link;
    if (href.startsWith("#")) return;
    try {
      const url = new URL(href, anchor.baseURI);
      if (anchor.hasAttribute("download") && url.protocol === "blob:") return;
      event.preventDefault();
      if (url.protocol === "http:" || url.protocol === "https:") open(url.href);
      else notify("warn", `Cannot open a ${url.protocol} link.`);
    } catch (error: unknown) {
      event.preventDefault();
      notify("warn", `Could not open link: ${String(error)}`);
    }
  };
  document.addEventListener("click", navigate);
  document.addEventListener("auxclick", navigate);
  return () => {
    document.removeEventListener("click", navigate);
    document.removeEventListener("auxclick", navigate);
  };
}
