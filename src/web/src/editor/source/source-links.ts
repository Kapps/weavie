import { type ClientSession, registerHostFeature } from "../../bridge";
import { openUrlExternal } from "../../terminal/terminal-links";
import type { EditorSessionEntry } from "../session-types";
import { tabKind } from "../tab-entry";
import { openSourceTarget, type SourceDocEntry, sourceDoc } from "./source-store";

// Per backend: the link hosts of its connected sources (SourceLinks.Claims on the host), held here so a click
// routes synchronously and a browser shell's window.open keeps the click's user gesture.
const linkHosts = new Map<string, readonly string[]>();

/** True when `url` is an http(s) URL on one of `hosts`; a leading-dot entry claims every subdomain. */
export function claimsLink(hosts: readonly string[], url: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  const host = parsed.hostname.toLowerCase();
  return (
    (parsed.protocol === "http:" || parsed.protocol === "https:") &&
    hosts.some((entry) =>
      entry.startsWith(".") ? host.endsWith(entry.toLowerCase()) : host === entry.toLowerCase(),
    )
  );
}

/** Opens a clicked content link: a connected source's page in Weavie's viewer, anything else in the browser. */
export function openLink(session: ClientSession | null, url: string): void {
  if (session !== null && claimsLink(linkHosts.get(session.connection.id) ?? [], url)) {
    openSourceTarget(session, url);
  } else {
    openUrlExternal(url);
  }
}

/** True when `doc` is a Notion page — the viewer's "Open in Notion" button and command act on it. */
export function isNotionPage(doc: SourceDocEntry | undefined): boolean {
  return doc?.sourceId === "notion";
}

/** Opens a Notion tab's page in the system browser; false (declines) for any other tab. */
export function openSourceInBrowser(session: ClientSession, entry: EditorSessionEntry): boolean {
  if (tabKind(entry) !== "source" || !isNotionPage(sourceDoc(session, entry.path))) return false;
  openUrlExternal(entry.path);
  return true;
}

registerHostFeature((connection) => {
  const offHello = connection.onHello((hello) => {
    linkHosts.set(connection.id, hello.sourceLinkHosts);
  });
  const offHosts = connection.host
    .feature("sources")
    .on<{ hosts: string[] }>("linkHosts", ({ hosts }) => {
      linkHosts.set(connection.id, hosts);
    });
  return () => {
    linkHosts.delete(connection.id);
    offHello();
    offHosts();
  };
});
