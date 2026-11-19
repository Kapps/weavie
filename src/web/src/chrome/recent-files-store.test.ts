import { describe, expect, it, vi } from "vitest";

vi.mock("../bridge", () => ({
  registerHostFeature: () => {},
  selectedSession: () => null,
}));
// The store under test is built by hand; the per-session wiring would need a page.
vi.mock("../files/session-file-index", () => ({ selectedFileIndex: () => null }));

const { FileIndex } = await import("../files/file-index");
const { projectRecentFiles } = await import("./recent-files-store");

function indexOf(root: string, paths: string[]) {
  const index = new FileIndex();
  index.setWorkspace(root, null, root.startsWith("/") ? "/" : "\\");
  const listing = index.newListing();
  for (const path of paths) listing.add(path);
  index.install(listing);
  return index;
}

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
