import { describe, expect, it } from "vitest";
import { type FileIndexChange, FileIndexSync } from "./file-index-sync";

function harness() {
  const applied: number[] = [];
  let downloads = 0;
  const sync = new FileIndexSync(
    (change) => applied.push(change.revision),
    () => {
      downloads++;
      sync.begin();
    },
  );
  const change = (revision: number): FileIndexChange => ({ revision, added: [], removed: [] });
  return { sync, applied, downloads: () => downloads, change };
}

describe("FileIndexSync", () => {
  it("applies the next revision and ignores ones the list already holds", () => {
    const { sync, applied, downloads, change } = harness();
    sync.begin();
    sync.complete(5, true);
    sync.changed(change(5));
    sync.changed(change(4));
    sync.changed(change(6));
    sync.changed(change(7));
    expect(applied).toEqual([6, 7]);
    expect(sync.known).toBe(7);
    expect(downloads()).toBe(0);
  });

  it("holds changes published during a download and steps the downloaded list through them", () => {
    const { sync, applied, downloads, change } = harness();
    sync.begin();
    sync.changed(change(4));
    sync.changed(change(5));
    sync.changed(change(6));
    sync.complete(4, true);
    expect(applied).toEqual([5, 6]);
    expect(sync.known).toBe(6);
    expect(downloads()).toBe(0);
  });

  it("downloads again when a revision is missing", () => {
    const { sync, applied, downloads, change } = harness();
    sync.begin();
    sync.complete(5, true);
    sync.changed(change(7));
    expect(applied).toEqual([]);
    expect(downloads()).toBe(1);
    sync.changed(change(8));
    sync.complete(8, true);
    expect(applied).toEqual([]);
    expect(sync.known).toBe(8);
  });

  it("downloads again when the host replaces the list, unless the list in hand is already that new", () => {
    const { sync, downloads } = harness();
    sync.begin();
    sync.complete(5, true);
    sync.replaced(5);
    expect(downloads()).toBe(0);
    sync.replaced(6);
    expect(downloads()).toBe(1);
  });

  it("downloads again when a replacement was published during a download that predates it", () => {
    const { sync, downloads } = harness();
    sync.begin();
    sync.replaced(9);
    sync.complete(8, true);
    expect(downloads()).toBe(1);
  });

  it("downloads on a change that arrives before any list", () => {
    const { sync, downloads, change } = harness();
    sync.changed(change(3));
    expect(downloads()).toBe(1);
  });

  it("refuses an unchanged answer for a revision it does not hold", () => {
    const { sync } = harness();
    sync.begin();
    sync.complete(5, true);
    sync.begin();
    expect(() => sync.complete(6, false)).toThrow();
  });

  it("keeps the list in hand when a download is abandoned", () => {
    const { sync, applied, change } = harness();
    sync.begin();
    sync.complete(5, true);
    sync.begin();
    sync.changed(change(6));
    sync.abandon();
    expect(sync.known).toBe(5);
    sync.changed(change(6));
    expect(applied).toEqual([6]);
  });
});
