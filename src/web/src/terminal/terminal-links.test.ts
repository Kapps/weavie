import type { ILink, Terminal } from "@xterm/xterm";
import { beforeEach, describe, expect, it, vi } from "vitest";

// terminal-links posts reveal-file to the ACTIVE backend and open-url to the LOCAL host (or window.open in a
// browser shell); capture each channel separately so the routing itself is pinned.
const posted = vi.hoisted(() => [] as unknown[]);
const postedLocal = vi.hoisted(() => [] as unknown[]);
const browserShell = vi.hoisted(() => ({ value: false }));
vi.mock("../bridge", () => ({
  postToHost: (m: unknown) => {
    posted.push(m);
  },
  postToLocalHost: (m: unknown) => {
    postedLocal.push(m);
  },
  isBrowserHostedShell: () => browserShell.value,
}));

const { wireTerminalLinks } = await import("./terminal-links");

// A minimal xterm stand-in: one buffer line of text, capturing the registered link provider + link handler.
function fakeTerminal(line: string): {
  term: Terminal;
  provide: () => ILink[];
  activateOsc: (uri: string) => void;
} {
  let provider: Parameters<Terminal["registerLinkProvider"]>[0] | undefined;
  const term = {
    options: {} as Terminal["options"],
    buffer: {
      active: {
        getLine: (n: number) => (n === 0 ? { translateToString: () => line } : undefined),
      },
    },
    registerLinkProvider: (p: Parameters<Terminal["registerLinkProvider"]>[0]) => {
      provider = p;
      return { dispose: () => {} };
    },
  } as unknown as Terminal;

  wireTerminalLinks(term);

  return {
    term,
    provide: () => {
      let links: ILink[] = [];
      provider?.provideLinks(1, (l) => {
        links = l ?? [];
      });
      return links;
    },
    activateOsc: (uri: string) =>
      term.options.linkHandler?.activate({} as MouseEvent, uri, {} as never),
  };
}

beforeEach(() => {
  posted.length = 0;
  postedLocal.length = 0;
  browserShell.value = false;
});

describe("auto-link provider", () => {
  it("links a bare file:line and posts reveal-file with the parsed line", () => {
    const { provide } = fakeTerminal("see src/foo.ts:42 for details");
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("src/foo.ts:42");
    links[0]?.activate({} as MouseEvent, links[0].text);
    expect(posted).toContainEqual({ type: "reveal-file", path: "src/foo.ts", line: 42 });
  });

  it("keeps a Windows drive colon in the path, splitting only the trailing :line", () => {
    const { provide } = fakeTerminal("at C:\\src\\foo.ts:7:3 now");
    const links = provide();
    links[0]?.activate({} as MouseEvent, links[0].text);
    expect(posted).toContainEqual({ type: "reveal-file", path: "C:\\src\\foo.ts", line: 7 });
  });

  it("links a bare URL and posts open-url to the LOCAL host (the browser is the user's, not the backend's)", () => {
    const { provide } = fakeTerminal("visit https://example.com/x?y=1 today");
    const links = provide();
    expect(links[0]?.text).toBe("https://example.com/x?y=1");
    links[0]?.activate({} as MouseEvent, links[0].text);
    expect(postedLocal).toContainEqual({ type: "open-url", url: "https://example.com/x?y=1" });
    expect(posted).toEqual([]);
  });

  it("opens a URL via window.open in a browser-served shell (its headless host has no browser)", () => {
    browserShell.value = true;
    const open = vi.fn();
    vi.stubGlobal("window", { open });
    const { provide } = fakeTerminal("visit https://example.com/ today");
    provide()[0]?.activate({} as MouseEvent, "https://example.com/");
    expect(open).toHaveBeenCalledWith("https://example.com/", "_blank", "noopener");
    expect(postedLocal).toEqual([]);
    vi.unstubAllGlobals();
  });

  it("leaves trailing sentence punctuation out of a URL", () => {
    const { provide } = fakeTerminal(
      "PR is up: https://github.com/Kapps/weavie/pull/186. Let me check CI",
    );
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("https://github.com/Kapps/weavie/pull/186");
  });

  it("links a bare path (no :line) inside a tool-call wrapper like Edit(...)", () => {
    const { provide } = fakeTerminal("⏺ Edit(src/web/src/terminal/terminal-links.ts)");
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("src/web/src/terminal/terminal-links.ts");
    links[0]?.activate({} as MouseEvent, links[0].text);
    expect(posted).toContainEqual({
      type: "reveal-file",
      path: "src/web/src/terminal/terminal-links.ts",
      line: 1,
    });
  });

  it("links a tool-call path with a :line once, at that line", () => {
    const { provide } = fakeTerminal("⏺ Read(src/foo.ts:42)");
    const links = provide();
    expect(links).toHaveLength(1);
    links[0]?.activate({} as MouseEvent, links[0].text);
    expect(posted).toContainEqual({ type: "reveal-file", path: "src/foo.ts", line: 42 });
  });

  it("links a URL inside a tool-call wrapper as a URL, not a reveal-file", () => {
    const { provide } = fakeTerminal("⏺ WebFetch(https://example.com/page.html)");
    const links = provide();
    expect(links).toHaveLength(1);
    links[0]?.activate({} as MouseEvent, links[0].text);
    expect(postedLocal).toContainEqual({ type: "open-url", url: "https://example.com/page.html" });
    expect(posted).toEqual([]);
  });

  it("does not link tool-call args that aren't file paths", () => {
    expect(fakeTerminal("⏺ Bash(git status)").provide()).toEqual([]);
  });

  it("does not double-link a URL that ends in a .ext:line-looking path", () => {
    // URLs are claimed first, so the file:line scanner must skip the span already inside the URL.
    const { provide } = fakeTerminal("https://host/app.js:10");
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("https://host/app.js:10");
  });

  it("returns no links for a plain line", () => {
    expect(fakeTerminal("just some prose here").provide()).toEqual([]);
  });
});

describe("OSC 8 link handler", () => {
  it("reveals a file:// URI at its line hash", () => {
    fakeTerminal("").activateOsc("file:///home/user/a.ts#12");
    expect(posted).toContainEqual({ type: "reveal-file", path: "/home/user/a.ts", line: 12 });
  });

  it("opens an http(s) URI via the LOCAL host", () => {
    fakeTerminal("").activateOsc("https://example.com/");
    expect(postedLocal).toContainEqual({ type: "open-url", url: "https://example.com/" });
  });

  it("ignores an unparseable URI without posting", () => {
    fakeTerminal("").activateOsc("not a uri");
    expect(posted).toEqual([]);
    expect(postedLocal).toEqual([]);
  });
});
