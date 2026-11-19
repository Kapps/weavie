import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const owner = {} as ClientSession;

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

const { dirtyPathsFor, isDirtyPath, setDirtyPath } = await import("./dirty-store");

beforeEach(() => {
  for (const path of dirtyPathsFor(owner)) {
    setDirtyPath(owner, path, false);
  }
});

describe("setDirtyPath", () => {
  it("adds and removes paths from the owning session", () => {
    setDirtyPath(owner, "c:/a.ts", true);
    expect(isDirtyPath(owner, "c:/a.ts")).toBe(true);
    setDirtyPath(owner, "c:/a.ts", false);
    expect(isDirtyPath(owner, "c:/a.ts")).toBe(false);
  });

  it("matches across path spellings", () => {
    setDirtyPath(owner, "\\home\\user\\a.ts", true);
    expect(isDirtyPath(owner, "/home/user/a.ts")).toBe(true);
    setDirtyPath(owner, "C:\\Src\\B.ts", true);
    expect(isDirtyPath(owner, "c:/src/b.ts")).toBe(true);
    setDirtyPath(owner, "/home/user/a.ts", false);
    expect(isDirtyPath(owner, "\\home\\user\\a.ts")).toBe(false);
  });

  it("does not allocate a new set on a no-op change", () => {
    setDirtyPath(owner, "c:/a.ts", true);
    const reference = dirtyPathsFor(owner);
    setDirtyPath(owner, "c:/a.ts", true);
    expect(dirtyPathsFor(owner)).toBe(reference);
    setDirtyPath(owner, "c:/b.ts", false);
    expect(dirtyPathsFor(owner)).toBe(reference);
  });
});
