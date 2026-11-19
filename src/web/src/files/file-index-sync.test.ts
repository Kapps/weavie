import { describe, expect, it } from "vitest";
import { type FileIndexChange, FileIndexSync } from "./file-index-sync";

function harness() {
  const applied: number[] = [];
  const state = { downloads: 0, fits: true };
  const sync = new FileIndexSync(
    (change) => {
      if (state.fits) applied.push(change.revision);
      return state.fits;
    },
    () => {
      state.downloads++;
      sync.begin();
    },
  );
  const change = (revision: number): FileIndexChange => ({ revision, added: [], removed: [] });
  return { sync, applied, state, change };
}

describe("FileIndexSync", () => {
  it("applies the next revision and ignores ones the list already holds", () => {
    const { sync, applied, state, change } = harness();
    sync.complete(5, true);
    sync.changed(change(5));
    sync.changed(change(4));
    sync.changed(change(6));
    sync.changed(change(7));
    expect(applied).toEqual([6, 7]);
    expect(sync.known).toBe(7);
    expect(state.downloads).toBe(0);
  });

  it("holds changes that arrive before the first list and steps that list through them", () => {
    const { sync, applied, state, change } = harness();
    sync.changed(change(4));
    sync.changed(change(5));
    sync.changed(change(6));
    expect(state.downloads).toBe(0);
    sync.complete(4, true);
    expect(applied).toEqual([5, 6]);
    expect(sync.known).toBe(6);
    expect(state.downloads).toBe(0);
  });

  it("downloads again when a revision is missing", () => {
    const { sync, applied, state, change } = harness();
    sync.complete(5, true);
    sync.changed(change(7));
    expect(applied).toEqual([]);
    expect(state.downloads).toBe(1);
    sync.changed(change(8));
    sync.complete(8, true);
    expect(applied).toEqual([]);
    expect(sync.known).toBe(8);
  });

  it("downloads again when the next revision does not fit the list in hand", () => {
    const { sync, state, change } = harness();
    sync.complete(5, true);
    state.fits = false;
    sync.changed(change(6));
    expect(sync.known).toBe(5);
    expect(state.downloads).toBe(1);
  });

  it("downloads again when the host replaces the list, unless the list in hand is already that new", () => {
    const { sync, state } = harness();
    sync.complete(5, true);
    sync.replaced(5);
    expect(state.downloads).toBe(0);
    sync.replaced(6);
    expect(state.downloads).toBe(1);
  });

  it("downloads again when a replacement was published during a download that predates it", () => {
    const { sync, state } = harness();
    sync.replaced(9);
    sync.complete(8, true);
    expect(state.downloads).toBe(1);
  });

  it("refuses an unchanged answer for a revision it does not hold", () => {
    const { sync } = harness();
    sync.complete(5, true);
    sync.begin();
    expect(() => sync.complete(6, false)).toThrow();
  });

  it("steps the list in hand through what waited on a download that failed", () => {
    const { sync, applied, state, change } = harness();
    sync.complete(5, true);
    sync.begin();
    sync.changed(change(6));
    sync.changed(change(7));
    sync.abandon();
    expect(applied).toEqual([6, 7]);
    expect(sync.known).toBe(7);
    expect(state.downloads).toBe(0);
  });

  it("drops what waited when the first download fails, and downloads on the next change", () => {
    const { sync, state, change } = harness();
    sync.changed(change(3));
    sync.changed(change(4));
    sync.abandon();
    expect(state.downloads).toBe(0);
    sync.changed(change(5));
    expect(state.downloads).toBe(1);
  });
});
