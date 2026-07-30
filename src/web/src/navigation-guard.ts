// Weavie must never navigate away from itself. An anchor click whose default action would replace the app —
// a web link in a rendered markdown preview, a source doc, or any surface without its own handler — is
// intercepted at the document and routed to the OS browser instead (URLs open in the user's browser or the
// explicit in-app web tab, never over Weavie). Listening at the bubble phase keeps surface-specific handlers
// (agent-pane links, terminal links) in charge: they preventDefault first and this guard then declines. The
// native hosts' webview policies backstop programmatic escapes (window.open, target=_blank, iframe top-nav).
// The opener is injected (openUrlExternal) so this module stays import-pure for the node test environment.

/**
 * The URL a clicked anchor would top-navigate the app to, or null when its default action is harmless: a
 * non-web scheme (command:, mailto:), an unparsable href, or an in-page #hash on the current page (the
 * default scroll is wanted). Same-origin non-hash links still count — they'd replace the running app too.
 */
export function externalNavigationTarget(href: string, pageHref: string): string | null {
  let target: URL;
  let page: URL;
  try {
    target = new URL(href);
    page = new URL(pageHref);
  } catch {
    return null;
  }
  if (target.protocol !== "http:" && target.protocol !== "https:") {
    return null;
  }
  const samePage =
    target.origin === page.origin &&
    target.pathname === page.pathname &&
    target.search === page.search;
  if (samePage && target.hash !== "") {
    return null;
  }
  return target.href;
}

/** Installs the document-level guard: unhandled external-anchor clicks open externally, never navigate. */
export function installNavigationGuard(doc: Document, openExternal: (url: string) => void): void {
  doc.addEventListener("click", (event) => {
    if (event.defaultPrevented || event.button !== 0) {
      return;
    }
    const anchor = event
      .composedPath()
      .find((el): el is HTMLAnchorElement => el instanceof HTMLAnchorElement);
    if (anchor === undefined) {
      return;
    }
    const url = externalNavigationTarget(anchor.href, doc.location.href);
    if (url !== null) {
      event.preventDefault();
      openExternal(url);
    }
  });
}
