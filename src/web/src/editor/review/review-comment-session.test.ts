import { describe, expect, it, vi } from "vitest";
import type { monaco } from "../monaco-setup";
import { ReviewCommentSession } from "./review-comment-session";
import type { ReviewComments, ReviewFileDiff, ReviewFileView } from "./review-store";

function fixture() {
  const path = "/repo/file.ts";
  let text = "one\ntwo\nthree";
  let version = 1;
  let disposed = false;
  const content = new Set<() => void>();
  const closing = new Set<() => void>();
  const listen = (set: Set<() => void>, listener: () => void) => {
    set.add(listener);
    return { dispose: () => set.delete(listener) };
  };
  const model = {
    uri: { toString: () => `session-a:${path}` },
    isDisposed: () => disposed,
    getValue: () => text,
    getVersionId: () => version,
    getLineCount: () => text.split("\n").length,
    onDidChangeContent: (listener: () => void) => listen(content, listener),
    onWillDispose: (listener: () => void) => listen(closing, listener),
  } as unknown as monaco.editor.ITextModel;
  let diff: ReviewFileDiff = {
    path,
    name: "file.ts",
    revision: "head-one",
    rejected: [],
    baseline: "old",
    baselineExists: true,
    acceptedBaseline: "old",
    acceptedBaselineExists: true,
    current: text,
    currentExists: true,
  };
  let comments: ReviewComments = {
    number: 12,
    path,
    comments: [10, 11].map((id) => ({
      id,
      line: 2,
      side: "right",
      author: "reviewer",
      body: "review",
      createdAt: "",
      inReplyTo: 0,
    })),
  };
  const view: ReviewFileView = {
    summary: () => ({
      path,
      name: "file.ts",
      line: 1,
      added: 3,
      removed: 1,
      currentExists: diff.currentExists,
    }),
    diff: () => diff,
    comments: () => comments,
    loaded: () => true,
    pending: () => true,
    collapsed: () => false,
  };
  let files = [view];
  const post = vi.fn(async () => ({ ok: true }));
  const owner = new ReviewCommentSession(
    () => files,
    (path, model) => model.uri.toString() === `session-a:${path}`,
    post,
  );
  const context = owner.context(path)!;
  return {
    owner,
    context,
    model,
    post,
    edit: (value: string) => {
      text = value;
      version++;
      for (const listener of [...content]) listener();
    },
    replaceDiff: (value: Partial<ReviewFileDiff>) => {
      diff = { ...diff, ...value };
    },
    replaceComments: (value: Partial<ReviewComments>) => {
      comments = { ...comments, ...value };
    },
    removeFile: () => {
      files = [];
    },
    closeModel: () => {
      disposed = true;
      for (const listener of [...closing]) listener();
    },
    subscriptions: () => content.size + closing.size,
  };
}

describe("review comment location authority", () => {
  it("keeps drafts through same-file diff/comment repaint without a DOM or editor owner", () => {
    const f = fixture();
    const draft = f.owner.openNew(f.context, f.model, 2);
    f.owner.drafts.write(draft, "half written");
    f.replaceDiff({ baseline: "new accepted baseline" });
    f.replaceComments({ comments: [...f.context.comments] });
    f.owner.refresh();
    expect(f.owner.drafts.retained()).toEqual([draft]);
    expect(draft.state()).toMatchObject({ body: "half written", unavailable: "" });
    expect(f.subscriptions()).toBe(2);
    f.owner.dispose();
    expect(f.subscriptions()).toBe(0);
  });

  it("refuses an identical-text model from another exact session before subscribing", () => {
    const f = fixture();
    const foreign = {
      ...f.model,
      uri: { toString: () => "session-b:/repo/file.ts" },
    } as monaco.editor.ITextModel;
    expect(() => f.owner.openNew(f.context, foreign, 2)).toThrow("reviewed file changed");
    expect(f.subscriptions()).toBe(0);
    expect(f.owner.drafts.retained()).toEqual([]);
  });

  it("invalidates a new anchor immediately on a working-model edit but not a reply", async () => {
    const f = fixture();
    const fresh = f.owner.openNew(f.context, f.model, 2);
    const reply = f.owner.openReply(f.context, 10);
    f.owner.drafts.write(fresh, "new draft");
    f.owner.drafts.write(reply, "thread answer");
    f.edit("inserted\none\ntwo\nthree");
    expect(await f.owner.drafts.submit(fresh)).toBe(false);
    expect(fresh.state().body).toBe("new draft");
    expect(await f.owner.drafts.submit(reply)).toBe(true);
    expect(f.post).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ inReplyTo: 10 }));
  });

  it("revalidates authoritative file content at submission even before refresh notifications", async () => {
    const f = fixture();
    const draft = f.owner.openNew(f.context, f.model, 2);
    f.owner.drafts.write(draft, "draft");
    f.replaceDiff({ current: "different without a new commit" });
    expect(await f.owner.drafts.submit(draft)).toBe(false);
    expect(f.post).not.toHaveBeenCalled();
  });

  it("keeps removed-file drafts reachable and unsubscribes when the exact model dies", async () => {
    const f = fixture();
    const draft = f.owner.openNew(f.context, f.model, 2);
    f.owner.drafts.write(draft, "do not lose this");
    f.removeFile();
    f.owner.refresh();
    f.closeModel();
    expect(f.subscriptions()).toBe(0);
    expect(f.owner.drafts.retained()).toEqual([draft]);
    expect(await f.owner.drafts.submit(draft)).toBe(false);
    expect(f.post).not.toHaveBeenCalled();
  });

  it("never moves a reply to another PR or the other root on the same line", async () => {
    const f = fixture();
    const draft = f.owner.openReply(f.context, 10);
    f.owner.drafts.write(draft, "reply");
    f.replaceComments({ comments: f.context.comments.filter((comment) => comment.id !== 10) });
    expect(await f.owner.drafts.submit(draft)).toBe(false);
    const other = f.owner.openReply(f.context, 11);
    f.owner.drafts.write(other, "second");
    f.replaceComments({ number: 13 });
    expect(await f.owner.drafts.submit(other)).toBe(false);
    expect(f.post).not.toHaveBeenCalled();
  });

  it("cannot reacquire model subscriptions after the session closes", () => {
    const f = fixture();
    f.owner.dispose();
    expect(() => f.owner.openNew(f.context, f.model, 2)).toThrow("live session");
    expect(() => f.owner.openReply(f.context, 10)).toThrow("live session");
    expect(f.subscriptions()).toBe(0);
  });
});
