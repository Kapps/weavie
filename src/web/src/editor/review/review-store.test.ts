import { createRoot } from "solid-js";
import { describe, expect, it } from "vitest";
import type { ClientSession } from "../../bridge";
import { createReviewStore, type ReviewFile, type ReviewFileDiff } from "./review-store";

const firstFile: ReviewFile = {
  path: "/work/src/first.ts",
  name: "first.ts",
  added: 3,
  removed: 1,
  line: 4,
  currentExists: true,
};

const secondFile: ReviewFile = {
  path: "/work/src/second.ts",
  name: "second.ts",
  added: 1,
  removed: 0,
  line: 8,
  currentExists: true,
};

function diff(file: ReviewFile, baseline = "before", current = "after"): ReviewFileDiff {
  return {
    path: file.path,
    name: file.name,
    acceptedBaseline: baseline,
    acceptedBaselineExists: true,
    baseline,
    baselineExists: true,
    current,
    currentExists: file.currentExists,
  };
}

function session(): ClientSession {
  return {} as ClientSession;
}

describe("review store", () => {
  it("keeps one stable per-file projection while diff pushes update incrementally", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const client = session();
      store.select(client);
      store.setFiles(client, [firstFile, secondFile], "vs main");
      const firstView = store.overview().files[0]!;
      const secondView = store.overview().files[1]!;

      store.setDiff(client, diff(firstFile));

      expect(store.overview().files[0]).toBe(firstView);
      expect(store.overview().files[1]).toBe(secondView);
      expect(firstView.diff()?.current).toBe("after");
      expect(firstView.collapsed()).toBe(false);
      expect(secondView.diff()).toBeNull();
      expect(store.overview().fullyLoaded()).toBe(false);
      expect(store.overview().hasPending()).toBe(true);

      store.setDiff(client, diff(secondFile));

      expect(store.overview().fullyLoaded()).toBe(true);
      expect(store.overview().hasPending()).toBe(true);
      dispose();
    });
  });

  it("un-reviews a folded file when new changes land, and follows authoritative keep transitions", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const client = session();
      store.setFiles(client, [firstFile], "turn");
      store.setDiff(client, diff(firstFile));
      const view = store.board(client).files[0]!;

      // A re-push of the same state leaves the fold the user made alone.
      store.setFileCollapsed(client, firstFile.path, true);
      store.setDiff(client, diff(firstFile));
      expect(view.collapsed()).toBe(true);

      // Reviewed is a claim about one exact state: new content in the file reopens it for review.
      store.setDiff(client, diff(firstFile, "before", "another pending version"));
      expect(view.collapsed()).toBe(false);

      store.setDiff(client, {
        ...diff(firstFile),
        acceptedBaseline: "before",
        baseline: "kept",
        current: "kept",
      });
      expect(view.collapsed()).toBe(true);

      store.setDiff(client, {
        ...diff(firstFile),
        acceptedBaseline: "before",
        baseline: "kept",
        current: "pending again",
      });
      expect(view.collapsed()).toBe(false);
      dispose();
    });
  });

  it("starts a file whose only diff is kept in its collapsed state", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const client = session();
      store.setDiff(client, {
        ...diff(firstFile),
        acceptedBaseline: "before",
        baseline: "kept",
        current: "kept",
      });
      store.setFiles(client, [firstFile], "turn");

      expect(store.board(client).files[0]?.collapsed()).toBe(true);

      store.setFileCollapsed(client, firstFile.path, false);
      store.setDiff(client, {
        ...diff(firstFile),
        acceptedBaseline: "before",
        baseline: "kept",
        current: "kept",
      });
      expect(store.board(client).files[0]?.collapsed()).toBe(false);
      dispose();
    });
  });

  it("reports a fully kept file as loaded, not as still loading", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const client = session();
      store.select(client);
      store.setFiles(client, [firstFile], "turn");
      const view = store.board(client).files[0]!;

      expect(view.loaded()).toBe(false);
      expect(store.overview().fullyLoaded()).toBe(false);

      store.setDiff(client, {
        ...diff(firstFile),
        acceptedBaseline: "kept",
        baseline: "kept",
        current: "kept",
      });

      expect(view.diff()).toBeNull();
      expect(view.loaded()).toBe(true);
      expect(store.overview().fullyLoaded()).toBe(true);
      dispose();
    });
  });

  it("retains a diff or comments that arrive before the review file list", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const client = session();
      const pushed = diff(firstFile);
      store.setDiff(client, pushed);
      store.setComments(client, { number: 42, path: firstFile.path, comments: [] });
      store.setFiles(client, [firstFile], "PR #42");
      store.select(client);

      const view = store.overview().files[0]!;
      expect(view.diff()).toBe(pushed);
      expect(view.comments()?.number).toBe(42);
      expect(store.overview().fullyLoaded()).toBe(true);
      expect(store.overview().added).toBe(3);
      expect(store.overview().removed).toBe(1);
      dispose();
    });
  });

  it("retains an empty-file diff whose existence changed", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const client = session();
      const deleted = { ...firstFile, added: 0, removed: 0, currentExists: false };
      store.select(client);
      store.setFiles(client, [deleted], "vs HEAD");
      store.setDiff(client, {
        ...diff(deleted, "", ""),
        acceptedBaselineExists: true,
        baselineExists: true,
      });

      expect(store.overview().files[0]?.diff()).not.toBeNull();
      expect(store.overview().hasPending()).toBe(true);
      dispose();
    });
  });

  it("isolates mode, cursor, files, and counts by session", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const left = session();
      const right = session();
      store.setFiles(left, [firstFile], "left");
      store.setFiles(right, [secondFile], "right");
      store.setDiff(left, diff(firstFile));
      store.setDiff(right, diff(secondFile));
      store.setFileCollapsed(left, firstFile.path, true);
      store.enterUnified(left, { path: firstFile.path, line: 12 });
      store.enterUnified(right, { path: secondFile.path, line: 19 });

      store.select(left);
      expect(store.mode()).toBe("unified");
      expect(store.count()).toBe(1);
      expect(store.overview().label).toBe("left");
      expect(store.overview().cursor).toEqual({ path: firstFile.path, line: 12 });
      expect(store.overview().files[0]?.collapsed()).toBe(true);

      store.select(right);
      expect(store.mode()).toBe("unified");
      expect(store.overview().files.map((file) => file.summary().path)).toEqual([secondFile.path]);
      expect(store.overview().cursor).toEqual({ path: secondFile.path, line: 19 });
      expect(store.overview().files[0]?.collapsed()).toBe(false);

      store.select(left);
      expect(store.board(left).cursor).toEqual({ path: firstFile.path, line: 12 });
      expect(store.overview().files[0]?.summary().path).toBe(firstFile.path);
      expect(store.overview().files[0]?.collapsed()).toBe(true);
      dispose();
    });
  });

  it("repairs the cursor when a file disappears and resets the selected projection", () => {
    createRoot((dispose) => {
      const store = createReviewStore();
      const client = session();
      store.setFiles(client, [firstFile, secondFile], "turn");
      store.enterUnified(client, { path: firstFile.path, line: 12 });
      store.setFiles(client, [secondFile], "turn");

      expect(store.board(client).cursor).toEqual({ path: secondFile.path, line: secondFile.line });

      store.select(client);
      store.reset(client);
      expect(store.mode()).toBe("file");
      expect(store.count()).toBe(0);
      expect(store.overview().files).toEqual([]);
      dispose();
    });
  });
});
