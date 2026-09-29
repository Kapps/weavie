import { describe, expect, it, vi } from "vitest";
import type { ReviewCopy } from "../editor-host";
import type { InlineDiffOptions } from "../inline-diff";
import { type ReviewFileConfiguration, ReviewFileOwner } from "./review-file-owner";

function fixture() {
  const original: ReviewFileConfiguration = {
    diff: {
      path: "/file.ts",
      name: "file.ts",
      revision: "1",
      rejected: [],
      baseline: "before",
      acceptedBaseline: "before",
      current: "after",
      baselineExists: true,
      acceptedBaselineExists: true,
      currentExists: true,
    },
    options: { mode: "applied", original: "before", claudeVersion: "after" },
  };
  let current: ReviewFileConfiguration | undefined = original;
  const entries = new Map<string, InlineDiffOptions>();
  const configure = vi.fn((uri: string, options: InlineDiffOptions | undefined) => {
    if (options === undefined) entries.delete(uri);
    else entries.set(uri, options);
  });
  const resolve = vi.fn<(diff: ReviewFileConfiguration["diff"]) => Promise<ReviewCopy>>();
  const owner = new ReviewFileOwner({ configure }, () => current, resolve);
  const copy = (uri: string): ReviewCopy => ({
    model: { uri: { toString: () => uri }, isDisposed: () => false } as ReviewCopy["model"],
    editable: !uri.includes("deleted="),
  });
  const set = (next: ReviewFileConfiguration | undefined): void => {
    current = next;
    owner.refresh();
  };
  return { owner, original, entries, configure, resolve, copy, set };
}

describe("unified file configuration ownership", () => {
  it("does not resolve unopened files and uses the copy's exact deleted-snapshot URI", async () => {
    const f = fixture();
    f.set({ ...f.original, diff: { ...f.original.diff, currentExists: false } });
    expect(f.resolve).not.toHaveBeenCalled();
    expect(f.configure).not.toHaveBeenCalled();
    const deleted = f.copy("weavie-review:/file.ts?deleted=scope");
    f.resolve.mockResolvedValue(deleted);
    expect(await f.owner.open()).toBe(deleted);
    expect([...f.entries.keys()]).toEqual([deleted.model.uri.toString()]);
    expect(f.entries.values().next().value).toBe(f.original.options);
    f.owner.dispose();
    expect(f.entries.size).toBe(0);
  });

  it("registers latest options when a same-existence diff changes during resolution", async () => {
    const f = fixture();
    let finish!: (copy: ReviewCopy) => void;
    f.resolve.mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    const pending = f.owner.open();
    const latest = { ...f.original, options: { ...f.original.options, onNextFile: vi.fn() } };
    f.set(latest);
    const copy = f.copy("weavie-file:/file.ts");
    finish(copy);
    await pending;
    expect(f.entries.get(copy.model.uri.toString())).toBe(latest.options);
    const calls = f.configure.mock.calls.length;
    f.resolve.mockResolvedValue(copy);
    await f.owner.open();
    expect(f.configure).toHaveBeenCalledTimes(calls);
    f.owner.dispose();
  });

  it.each([
    "removed",
    "closed",
    "restored",
  ] as const)("rejects a late copy when %s", async (kind) => {
    const f = fixture();
    f.set({ ...f.original, diff: { ...f.original.diff, currentExists: false } });
    let finish!: (copy: ReviewCopy) => void;
    f.resolve.mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    const rejected = expect(f.owner.open()).rejects.toMatchObject({ name: "AbortError" });
    if (kind === "closed") f.owner.dispose();
    else f.set(kind === "removed" ? undefined : f.original);
    finish(f.copy("weavie-review:/file.ts?deleted=scope"));
    await rejected;
    expect(f.entries.size).toBe(0);
    expect(f.configure).not.toHaveBeenCalled();
    f.owner.dispose();
  });

  it("clears the old URI on deletion/restoration and invalidates A→B→A pending opens", async () => {
    const f = fixture();
    const working = f.copy("weavie-file:/file.ts");
    f.resolve.mockResolvedValue(working);
    await f.owner.open();
    let finish!: (copy: ReviewCopy) => void;
    f.resolve.mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve;
      }),
    );
    const rejected = expect(f.owner.open()).rejects.toMatchObject({ name: "AbortError" });
    f.set({ ...f.original, diff: { ...f.original.diff, currentExists: false } });
    expect(f.entries.size).toBe(0);
    f.set(f.original);
    finish(working);
    await rejected;
    expect(f.entries.size).toBe(0);
    await f.owner.open();
    expect(f.entries.has(working.model.uri.toString())).toBe(true);
    f.owner.dispose();
  });

  it("cannot return a copy invalidated by a registration observer", async () => {
    const f = fixture();
    f.resolve.mockResolvedValue(f.copy("weavie-file:/file.ts"));
    const register = f.configure.getMockImplementation()!;
    f.configure.mockImplementation((uri, options) => {
      register(uri, options);
      if (options !== undefined) f.owner.dispose();
    });
    await expect(f.owner.open()).rejects.toMatchObject({ name: "AbortError" });
    expect(f.entries.size).toBe(0);
  });
});
