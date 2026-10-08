import { beforeAll, describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../../bridge";

const harness = vi.hoisted(() => ({
  installer: undefined as ((connection: HostConnection) => unknown) | undefined,
  external: [] as string[],
  sourceOpens: [] as string[],
}));
vi.mock("../../bridge", () => ({
  registerHostFeature: (installer: (connection: HostConnection) => unknown) => {
    harness.installer = installer;
  },
}));
vi.mock("../../terminal/terminal-links", () => ({
  openUrlExternal: (url: string) => harness.external.push(url),
}));
vi.mock("./source-store", () => ({
  openSourceTarget: (_session: ClientSession, url: string) => harness.sourceOpens.push(url),
  sourceDoc: () => undefined,
}));

const { claimsLink, openLink } = await import("./source-links");

const NOTION = ["notion.so", ".notion.so", ".notion.site", "app.notion.com"];
const PAGE = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d";

describe("claimsLink", () => {
  it.each([
    [PAGE, true],
    ["https://notion.so/1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", true],
    ["https://acme.notion.site/Public-Page", true],
    ["https://APP.Notion.com/p/Test-Page", true],
    ["https://www.notion.com/product", false],
    ["https://notnotion.so/page", false],
    ["ftp://www.notion.so/page", false],
    ["not a url", false],
  ])("%s → %s", (url, expected) => {
    expect(claimsLink(NOTION, url)).toBe(expected);
  });
});

describe("openLink", () => {
  const listeners = new Map<string, (payload: never) => void>();
  const connected = { id: "remote-a" } as unknown as HostConnection;
  const disconnected = { id: "remote-b" } as unknown as HostConnection;
  const sessionOn = (connection: HostConnection) => ({ connection }) as unknown as ClientSession;

  beforeAll(() => {
    for (const connection of [connected, disconnected]) {
      Object.assign(connection, {
        onHello: (listener: (hello: { sourceLinkHosts: string[] }) => void) => {
          listener({ sourceLinkHosts: [] });
          return () => {};
        },
        host: {
          feature: () => ({
            on: (name: string, listener: (payload: never) => void) => {
              listeners.set(`${connection.id}:${name}`, listener);
              return () => {};
            },
          }),
        },
      });
      harness.installer?.(connection);
    }
    // Connecting Notion on one backend pushes its link hosts to that backend's clients only.
    (listeners.get("remote-a:linkHosts") as (payload: { hosts: string[] }) => void)({
      hosts: NOTION,
    });
  });

  it("opens a connected source's page in Weavie's viewer", () => {
    openLink(sessionOn(connected), PAGE);
    expect(harness.sourceOpens).toEqual([PAGE]);
  });

  it("opens the page in the browser when its backend has no connected source", () => {
    openLink(sessionOn(disconnected), PAGE);
    expect(harness.external).toContain(PAGE);
  });

  it("opens any other link in the browser", () => {
    openLink(sessionOn(connected), "https://github.com/owner/repo");
    expect(harness.external).toContain("https://github.com/owner/repo");
  });
});
