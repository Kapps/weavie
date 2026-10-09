import { Fzf } from "fzf";
import { describe, expect, it, vi } from "vitest";
import { createFileFinder, type FileRow, rankFiles, splitPath } from "./file-search";

vi.mock("fzf", { spy: true });

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

  it("bounds precision scoring for broad queries and each typed prefix", () => {
    const word = "controller";
    for (const query of [
      "s",
      "se",
      "zqwxnomatch",
      ...Array.from(word, (_, i) => word.slice(0, i + 1)),
    ]) {
      vi.mocked(Fzf).mockClear();
      const result = rankFiles(finder, query, [], null);
      expect(Fzf).toHaveBeenCalledOnce();
      const candidates = vi.mocked(Fzf).mock.calls[0]![0];
      expect(candidates.length, query).toBeLessThanOrEqual(2000);
      expect(result.matches.length, query).toBe(candidates.length);
    }
  });

  it("reports all broad-query matches beyond the precision-scored candidates", () => {
    const result = rankFiles(finder, "s", [], null);
    expect(result.total).toBe(rows.filter((row) => row.rel.toLowerCase().includes("s")).length);
    expect(result.total).toBeGreaterThan(result.matches.length);
  });

  it("ranks an exact filename match first", () => {
    expect(rankFiles(finder, "zebraquokkawidget", [], null).matches[0]?.row.leaf).toBe(
      "ZebraQuokkaWidget.ts",
    );
  });

  it("ranks a basename's camelCase initials first", () => {
    expect(rankFiles(finder, "zqw", [], null).matches[0]?.row.leaf).toBe("ZebraQuokkaWidget.ts");
  });

  it("returns nothing when the query is not a subsequence of any path", () => {
    expect(rankFiles(finder, "qqzzxxjjww", [], null).matches).toHaveLength(0);
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
    rankFiles(finder, "config", recent, currentDir).matches.map((s) => s.row.dir);

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

describe("file match counts", () => {
  it("agrees with precision scoring for spaces, mixed case and fuzzy paths", () => {
    const rows = ["src/My File.ts", "src/MyFile.ts", "src/Many Fine Files.ts", "src/other.ts"].map(
      (rel) => splitPath(`/repo/${rel}`, "/repo"),
    );
    const finder = createFileFinder(rows);
    const fzf = new Fzf(rows, { selector: (row) => row.rel, casing: "case-insensitive" });
    for (const query of ["m f", "MY F", "s/mf", "MyFile", "missing"]) {
      const result = rankFiles(finder, query, [], null);
      expect(result.total, query).toBe(fzf.find(query).length);
      expect(result.matches, query).toHaveLength(result.total);
    }
  });
});
