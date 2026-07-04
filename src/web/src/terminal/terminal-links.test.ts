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

// A minimal xterm stand-in over an ordered set of buffer rows. Each row carries isWrapped (true = a soft-wrap
// continuation of the row above), so the provider's logical-line reconstruction can be exercised. `provide`
// queries a single buffer row (1-based), mirroring how xterm invokes the provider once per row.
function fakeTerminal(...rows: Array<{ text: string; isWrapped: boolean }>): {
  term: Terminal;
  provide: (row?: number) => ILink[];
  activateOsc: (uri: string) => void;
  hoveredUrl: () => string | undefined;
} {
  let provider: Parameters<Terminal["registerLinkProvider"]>[0] | undefined;
  const term = {
    options: {} as Terminal["options"],
    buffer: {
      active: {
        getLine: (n: number) => {
          const row = rows[n];
          return row ? { translateToString: () => row.text, isWrapped: row.isWrapped } : undefined;
        },
      },
    },
    registerLinkProvider: (p: Parameters<Terminal["registerLinkProvider"]>[0]) => {
      provider = p;
      return { dispose: () => {} };
    },
  } as unknown as Terminal;

  const hoveredUrl = wireTerminalLinks(term);

  return {
    term,
    provide: (row = 1) => {
      let links: ILink[] = [];
      provider?.provideLinks(row, (l) => {
        links = l ?? [];
      });
      return links;
    },
    activateOsc: (uri: string) =>
      term.options.linkHandler?.activate({ button: 0 } as MouseEvent, uri, {} as never),
    hoveredUrl,
  };
}

// The common single-line case: one un-wrapped row.
function oneLine(line: string): ReturnType<typeof fakeTerminal> {
  return fakeTerminal({ text: line, isWrapped: false });
}

beforeEach(() => {
  posted.length = 0;
  postedLocal.length = 0;
  browserShell.value = false;
});

describe("auto-link provider", () => {
  it("links a bare file:line and posts reveal-file with the parsed line", () => {
    const { provide } = oneLine("see src/foo.ts:42 for details");
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("src/foo.ts:42");
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(posted).toContainEqual({ type: "reveal-file", path: "src/foo.ts", line: 42 });
  });

  it("keeps a Windows drive colon in the path, splitting only the trailing :line", () => {
    const { provide } = oneLine("at C:\\src\\foo.ts:7:3 now");
    const links = provide();
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(posted).toContainEqual({ type: "reveal-file", path: "C:\\src\\foo.ts", line: 7 });
  });

  it("links a bare URL and posts open-url to the LOCAL host (the browser is the user's, not the backend's)", () => {
    const { provide } = oneLine("visit https://example.com/x?y=1 today");
    const links = provide();
    expect(links[0]?.text).toBe("https://example.com/x?y=1");
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(postedLocal).toContainEqual({ type: "open-url", url: "https://example.com/x?y=1" });
    expect(posted).toEqual([]);
  });

  it("opens a URL via window.open in a browser-served shell (its headless host has no browser)", () => {
    browserShell.value = true;
    const open = vi.fn();
    vi.stubGlobal("window", { open });
    const { provide } = oneLine("visit https://example.com/ today");
    provide()[0]?.activate({ button: 0 } as MouseEvent, "https://example.com/");
    expect(open).toHaveBeenCalledWith("https://example.com/", "_blank", "noopener");
    expect(postedLocal).toEqual([]);
    vi.unstubAllGlobals();
  });

  it("does not open a URL on a non-primary (right) click — the right-click menu handles it", () => {
    const { provide } = oneLine("visit https://example.com/ today");
    provide()[0]?.activate({ button: 2 } as MouseEvent, "https://example.com/");
    expect(postedLocal).toEqual([]);
    expect(posted).toEqual([]);
  });

  it("tracks the hovered URL for the right-click menu and clears it on leave", () => {
    const term = oneLine("visit https://example.com/x today");
    const link = term.provide()[0];
    expect(term.hoveredUrl()).toBeUndefined();
    link?.hover?.({} as MouseEvent, link.text);
    expect(term.hoveredUrl()).toBe("https://example.com/x");
    link?.leave?.({} as MouseEvent, link.text);
    expect(term.hoveredUrl()).toBeUndefined();
  });

  it("does not track a file reference as a hoverable URL (no browser/Weavie menu for it)", () => {
    const link = oneLine("see src/foo.ts:42 now").provide()[0];
    expect(link?.hover).toBeUndefined();
  });

  it("leaves trailing sentence punctuation out of a URL", () => {
    const { provide } = oneLine(
      "PR is up: https://github.com/Kapps/weavie/pull/186. Let me check CI",
    );
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("https://github.com/Kapps/weavie/pull/186");
  });

  it("links a bare path (no :line) inside a tool-call wrapper like Edit(...)", () => {
    const { provide } = oneLine("⏺ Edit(src/web/src/terminal/terminal-links.ts)");
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("src/web/src/terminal/terminal-links.ts");
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(posted).toContainEqual({
      type: "reveal-file",
      path: "src/web/src/terminal/terminal-links.ts",
      line: 1,
    });
  });

  it("links a tool-call path with a :line once, at that line", () => {
    const { provide } = oneLine("⏺ Read(src/foo.ts:42)");
    const links = provide();
    expect(links).toHaveLength(1);
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(posted).toContainEqual({ type: "reveal-file", path: "src/foo.ts", line: 42 });
  });

  it("links a URL inside a tool-call wrapper as a URL, not a reveal-file", () => {
    const { provide } = oneLine("⏺ WebFetch(https://example.com/page.html)");
    const links = provide();
    expect(links).toHaveLength(1);
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(postedLocal).toContainEqual({ type: "open-url", url: "https://example.com/page.html" });
    expect(posted).toEqual([]);
  });

  it("does not link tool-call args that aren't file paths", () => {
    expect(oneLine("⏺ Bash(git status)").provide()).toEqual([]);
  });

  it("links a bare path (separator, no :line, no wrapper) and reveals it at line 1", () => {
    const { provide } = oneLine("wrote src/web/e2e/.recordings/clip.webm just now");
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("src/web/e2e/.recordings/clip.webm");
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(posted).toContainEqual({
      type: "reveal-file",
      path: "src/web/e2e/.recordings/clip.webm",
      line: 1,
    });
  });

  it("links a rooted bare path, keeping the leading separator", () => {
    const { provide } = oneLine("see /home/user/notes.md for context");
    const links = provide();
    expect(links[0]?.text).toBe("/home/user/notes.md");
  });

  it("does not link a bare path as file:line and again as bare (single link at its line)", () => {
    const { provide } = oneLine("edit src/foo.ts:42 please");
    expect(provide()).toHaveLength(1);
  });

  it("does not link a dotted word with no separator (Node.js, package.json)", () => {
    expect(oneLine("built with Node.js; see package.json").provide()).toEqual([]);
  });

  it("does not link a slashed token whose extension starts with a digit (HTTP/1.1)", () => {
    expect(oneLine("the server speaks HTTP/1.1 here").provide()).toEqual([]);
  });

  it("does not double-link a URL that ends in a .ext:line-looking path", () => {
    // URLs are claimed first, so the file:line scanner must skip the span already inside the URL.
    const { provide } = oneLine("https://host/app.js:10");
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("https://host/app.js:10");
  });

  it("returns no links for a plain line", () => {
    expect(oneLine("just some prose here").provide()).toEqual([]);
  });
});

describe("soft-wrapped links", () => {
  it("reconstructs a file:line split across two wrapped rows; either row opens the full path", () => {
    const term = fakeTerminal(
      { text: "src/web/src/terminal/terminal-", isWrapped: false },
      { text: "links.ts:104:17", isWrapped: true },
    );
    const full = "src/web/src/terminal/terminal-links.ts:104:17";
    for (const row of [1, 2]) {
      posted.length = 0;
      const links = term.provide(row);
      expect(links).toHaveLength(1);
      expect(links[0]?.text).toBe(full);
      links[0]?.activate({ button: 0 } as MouseEvent, full);
      expect(posted).toContainEqual({
        type: "reveal-file",
        path: "src/web/src/terminal/terminal-links.ts",
        line: 104,
      });
    }
  });

  it("reconstructs a URL split across wrapped rows and opens the whole URL", () => {
    const { provide } = fakeTerminal(
      { text: "see https://github.com/Kapps/weavie/", isWrapped: false },
      { text: "pull/186/files now", isWrapped: true },
    );
    const links = provide();
    expect(links).toHaveLength(1);
    expect(links[0]?.text).toBe("https://github.com/Kapps/weavie/pull/186/files");
    links[0]?.activate({ button: 0 } as MouseEvent, links[0].text);
    expect(postedLocal).toContainEqual({
      type: "open-url",
      url: "https://github.com/Kapps/weavie/pull/186/files",
    });
  });

  it("stitches only across an isWrapped continuation, never across a real newline", () => {
    // Neither fragment matches alone ("src/foo" has no extension, ".ts" has no separator); only their join
    // "src/foo.ts" does. isWrapped=true is one line the terminal reflowed, so it links; isWrapped=false is
    // two lines the program printed, so it must not be stitched.
    const rows = [
      { text: "src/foo", isWrapped: false },
      { text: ".ts", isWrapped: true },
    ] as const;
    expect(fakeTerminal(...rows).provide(1)[0]?.text).toBe("src/foo.ts");

    const broken = fakeTerminal(rows[0], { text: ".ts", isWrapped: false });
    expect(broken.provide(1)).toEqual([]);
    expect(broken.provide(2)).toEqual([]);
  });
});

describe("OSC 8 link handler", () => {
  it("reveals a file:// URI at its line hash", () => {
    oneLine("").activateOsc("file:///home/user/a.ts#12");
    expect(posted).toContainEqual({ type: "reveal-file", path: "/home/user/a.ts", line: 12 });
  });

  it("opens an http(s) URI via the LOCAL host", () => {
    oneLine("").activateOsc("https://example.com/");
    expect(postedLocal).toContainEqual({ type: "open-url", url: "https://example.com/" });
  });

  it("ignores an unparseable URI without posting", () => {
    oneLine("").activateOsc("not a uri");
    expect(posted).toEqual([]);
    expect(postedLocal).toEqual([]);
  });

  it("does not open an OSC http(s) link on a non-primary (right) click", () => {
    const { term } = oneLine("");
    term.options.linkHandler?.activate(
      { button: 2 } as MouseEvent,
      "https://example.com/",
      {} as never,
    );
    expect(postedLocal).toEqual([]);
  });

  it("tracks a hovered OSC http(s) link for the right-click menu, clearing it on leave", () => {
    const { term, hoveredUrl } = oneLine("");
    term.options.linkHandler?.hover?.({} as MouseEvent, "https://example.com/", {} as never);
    expect(hoveredUrl()).toBe("https://example.com/");
    term.options.linkHandler?.leave?.({} as MouseEvent, "https://example.com/", {} as never);
    expect(hoveredUrl()).toBeUndefined();
  });

  it("does not offer the open-in menu for a non-http OSC link (leaves the hovered URL unset)", () => {
    const { term, hoveredUrl } = oneLine("");
    term.options.linkHandler?.hover?.({} as MouseEvent, "file:///home/user/a.ts", {} as never);
    expect(hoveredUrl()).toBeUndefined();
  });
});
