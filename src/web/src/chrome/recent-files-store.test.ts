import { createSignal } from "solid-js";
import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { FileIndex as SessionIndex } from "../files/file-index";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));
// The index under test is built by hand; the per-session wiring would need a page.
const held = vi.hoisted(() => ({
  index: (): SessionIndex | null => null,
  install: (_session: ClientSession): void => {},
}));
vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: (session: ClientSession) => () => void) => {
    held.install = installer;
    return () => {};
  },
}));
vi.mock("../files/session-file-index", () => ({ fileIndexFor: () => held.index() }));
vi.mock("../repo-state", () => ({ repoStateOf: () => ({ recentFiles: ["a.ts", "gone.ts"] }) }));

const { FileIndex } = await import("../files/file-index");
const { projectRecentFiles, recentFilesOf } = await import("./recent-files-store");

function indexOf(root: string, paths: string[]) {
  const index = new FileIndex();
  index.setWorkspace(root, null, root.startsWith("/") ? "/" : "\\");
  const listing = index.newListing();
  for (const path of paths) listing.add(path);
  index.install(listing);
  return index;
}

describe("recentFilesOf", () => {
  it("is loading until the session's index lands, then projects onto it once per change", () => {
    const session = {} as ClientSession;
    const [index, setIndex] = createSignal<SessionIndex>(new FileIndex());
    held.index = index;
    held.install(session);
    expect(recentFilesOf(null)).toEqual({ files: [], loading: false, hasHistory: false });
    expect(recentFilesOf(session)).toEqual({ files: [], loading: true, hasHistory: true });

    setIndex(indexOf("/repo", ["a.ts"]));
    const projected = recentFilesOf(session);
    expect(projected).toEqual({ files: ["/repo/a.ts"], loading: false, hasHistory: true });
    expect(recentFilesOf(session)).toBe(projected);
  });
});

describe("projectRecentFiles", () => {
  it("keeps frecency order and removes files absent from the selected index", () => {
    expect(
      projectRecentFiles(
        ["alpha-only.txt", "missing.txt", "src/common.ts"],
        indexOf("/worktrees/alpha", ["src/common.ts", "alpha-only.txt"]),
      ),
    ).toEqual(["/worktrees/alpha/alpha-only.txt", "/worktrees/alpha/src/common.ts"]);
  });

  it("returns an indexed Windows path once across case and separator aliases", () => {
    expect(
      projectRecentFiles(["DIR/foo.ts", "dir\\FOO.ts"], indexOf("C:\\Repo\\", ["Dir/Foo.TS"])),
    ).toEqual(["c:\\Repo\\Dir\\Foo.TS"]);
  });

  it("preserves distinct POSIX case and backslash paths", () => {
    expect(
      projectRecentFiles(
        ["Foo.ts", "foo.ts", "dir/foo.ts", "dir\\foo.ts", "FOO.ts"],
        indexOf("/repo", ["foo.ts", "Foo.ts", "dir\\foo.ts", "dir/foo.ts"]),
      ),
    ).toEqual(["/repo/Foo.ts", "/repo/foo.ts", "/repo/dir/foo.ts", "/repo/dir\\foo.ts"]);
  });
});
