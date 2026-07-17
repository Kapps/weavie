import { Fzf } from "fzf";
import { describe, expect, it } from "vitest";
import { createFileFinder, type FileRow, rankFiles, splitPath } from "./file-search";

// Well past the host's 20k index cap — the omnibar must stay snappy even if that ceiling is raised, and a
// regression to scoring the whole index per keystroke (the old behaviour) shows up here as a blown budget.
const INDEX_SIZE = 120_000;

// A unique filename no synthetic path can collide with, so exact-ranking assertions have one right answer.
const SENTINEL = "src/unique/place/ZebraQuokkaWidget.ts";

function buildIndex(n: number): FileRow[] {
  const dirs = [
    "src",
    "web",
    "core",
    "hosting",
    "components",
    "editor",
    "chrome",
    "commands",
    "workspaces",
    "sessions",
    "services",
    "utils",
    "models",
  ];
  const words = [
    "Session",
    "Controller",
    "Editor",
    "Index",
    "Workspace",
    "File",
    "Stream",
    "Reader",
    "Search",
    "Theme",
    "Bridge",
    "Host",
    "Command",
    "Process",
    "Supervisor",
    "Remote",
    "Agent",
    "Hook",
    "Permission",
    "Manager",
  ];
  const rows: FileRow[] = [];
  const push = (rel: string): void => {
    const slash = rel.lastIndexOf("/");
    rows.push({
      abs: `C:/proj/${rel}`,
      rel,
      leaf: rel.slice(slash + 1),
      dir: rel.slice(0, slash),
      leafStart: slash + 1,
    });
  };
  for (let i = 0; i < n; i++) {
    const a = dirs[i % dirs.length];
    const b = dirs[(i * 7) % dirs.length];
    const c = words[(i * 3) % words.length];
    const d = words[(i * 5) % words.length];
    const e = words[(i * 11) % words.length];
    push(`${a}/${b}/${c}${d}/${e}${i}.ts`);
  }
  push(SENTINEL);
  return rows;
}

describe("omnibar file search over a huge workspace", () => {
  const rows = buildIndex(INDEX_SIZE);
  const finder = createFileFinder(rows);

  it("answers every keystroke well under a budget at 120k files", () => {
    // Includes the worst case (1-2 char queries matching most of the index) and a no-match query. The naive
    // whole-index scorer takes 800ms+ for "se" on a contended CI box, so this budget still catches a
    // regression to that. Kept generous so it tracks the order-of-magnitude win, not raw box speed — the
    // machine-independent guard below is the real anchor.
    for (const q of [
      "s",
      "se",
      "sess",
      "session",
      "editor",
      "controller",
      "sc",
      "reader",
      "zqwxnomatch",
    ]) {
      const start = performance.now();
      rankFiles(finder, q, [], null);
      const ms = performance.now() - start;
      expect(ms, `query "${q}" took ${ms.toFixed(1)}ms`).toBeLessThan(400);
    }
  });

  it("is several times faster than scoring the whole index per keystroke", () => {
    // A machine-independent anchor: prove the structural win (pre-filter + capped re-score) against the naive
    // approach on the same data, so the test means something regardless of how fast the box is.
    const naive = new Fzf(rows, { selector: (r) => r.rel, casing: "case-insensitive" });

    const naiveStart = performance.now();
    naive.find("se");
    const naiveMs = performance.now() - naiveStart;

    const fastStart = performance.now();
    rankFiles(finder, "se", [], null);
    const fastMs = performance.now() - fastStart;

    expect(fastMs * 4).toBeLessThan(naiveMs);
  });

  it("stays responsive while typing a filename out one character at a time", () => {
    const word = "controller";
    const start = performance.now();
    for (let k = 1; k <= word.length; k++) {
      rankFiles(finder, word.slice(0, k), [], null);
    }
    const perKeystroke = (performance.now() - start) / word.length;
    expect(perKeystroke).toBeLessThan(160);
  });

  it("ranks an exact filename match first", () => {
    expect(rankFiles(finder, "zebraquokkawidget", [], null)[0]?.row.leaf).toBe(
      "ZebraQuokkaWidget.ts",
    );
  });

  it("ranks a basename's camelCase initials first", () => {
    expect(rankFiles(finder, "zqw", [], null)[0]?.row.leaf).toBe("ZebraQuokkaWidget.ts");
  });

  it("returns nothing when the query is not a subsequence of any path", () => {
    expect(rankFiles(finder, "qqzzxxjjww", [], null)).toHaveLength(0);
  });
});

describe("proximity to the active file", () => {
  const rels = [
    "config.ts",
    "src/config.ts",
    "src/app/config.ts",
    "src/app/deep/nested/config.ts",
    "src/other/config.ts",
  ];
  const finder = createFileFinder(rels.map((rel) => splitPath(`C:/proj/${rel}`, "C:/proj")));
  const dirsOf = (recent: readonly string[], currentDir: string | null): string[] =>
    rankFiles(finder, "config", recent, currentDir).map((s) => s.row.dir);

  it("ranks the config beside the active file first", () => {
    expect(dirsOf([], "src/app")[0]).toBe("src/app");
  });

  it("orders equal matches by tree distance from the active folder", () => {
    // From src/app: same dir (0), parent (1), then root / sibling / grandchild (all 2, length-tiebroken).
    expect(dirsOf([], "src/app")).toEqual([
      "src/app",
      "src",
      "",
      "src/other",
      "src/app/deep/nested",
    ]);
  });

  it("prefers proximity over recency", () => {
    expect(dirsOf(["C:/proj/src/other/config.ts"], "src/app")[0]).toBe("src/app");
  });

  it("falls back to recency when no file is active", () => {
    expect(dirsOf(["C:/proj/src/other/config.ts"], null)[0]).toBe("src/other");
  });

  it("compares folders case-insensitively", () => {
    expect(dirsOf([], "SRC/APP")[0]).toBe("src/app");
  });
});
