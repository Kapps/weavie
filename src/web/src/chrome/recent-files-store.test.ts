import { describe, expect, it, vi } from "vitest";

vi.mock("../bridge", () => ({
  registerHostFeature: () => {},
  selectedSession: () => null,
}));
vi.mock("../files/session-files", () => ({
  selectedFileIndex: () => ({ root: null, files: [], pending: false }),
}));

const { projectRecentFiles } = await import("./recent-files-store");

describe("projectRecentFiles", () => {
  it("keeps frecency order and removes files absent from the selected index", () => {
    expect(
      projectRecentFiles(["alpha-only.txt", "missing.txt", "src/common.ts"], "/worktrees/alpha", [
        "/worktrees/alpha/src/common.ts",
        "/worktrees/alpha/alpha-only.txt",
      ]),
    ).toEqual(["/worktrees/alpha/alpha-only.txt", "/worktrees/alpha/src/common.ts"]);
  });

  it("returns an indexed Windows path once across case and separator aliases", () => {
    expect(
      projectRecentFiles(["DIR/foo.ts", "dir\\FOO.ts"], "C:\\Repo\\", ["C:\\Repo\\Dir\\Foo.TS"]),
    ).toEqual(["C:\\Repo\\Dir\\Foo.TS"]);
  });

  it("preserves distinct POSIX case and backslash paths", () => {
    expect(
      projectRecentFiles(["Foo.ts", "foo.ts", "dir/foo.ts", "dir\\foo.ts", "FOO.ts"], "/repo", [
        "/repo/foo.ts",
        "/repo/Foo.ts",
        "/repo/dir\\foo.ts",
        "/repo/dir/foo.ts",
      ]),
    ).toEqual(["/repo/Foo.ts", "/repo/foo.ts", "/repo/dir/foo.ts", "/repo/dir\\foo.ts"]);
  });
});
