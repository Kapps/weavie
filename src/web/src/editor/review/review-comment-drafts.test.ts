import { describe, expect, it, vi } from "vitest";
import type { CommandResult } from "../../commands/types";
import { ReviewCommentDrafts } from "./review-comment-drafts";

const file = { number: 12, path: "/repo/file.ts" };
const anchor = { incarnation: {}, version: 4, revision: "head-one", current: "source", line: 8 };

function fixture() {
  let resolve!: (result: CommandResult) => void;
  let reject!: (error: Error) => void;
  const response = new Promise<CommandResult>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  const post = vi.fn(() => response);
  const validate = vi.fn(() => "");
  const drafts = new ReviewCommentDrafts(post, validate);
  const draft = drafts.open(file, { kind: "new", anchor });
  drafts.write(draft, " original ");
  return { drafts, draft, post, resolve, reject, validate };
}

describe("review comment draft ownership", () => {
  it("retains an empty composing reply without pinning idle empty replies", () => {
    const drafts = new ReviewCommentDrafts(vi.fn(), () => "");
    const reply = drafts.open(file, { kind: "reply", rootId: 10 });
    const end = drafts.beginComposition(reply);
    expect(drafts.retained()).toEqual([reply]);
    expect(() => drafts.discard(reply)).toThrow("active composition");
    drafts.write(reply, "intermediate");
    drafts.write(reply, "");
    expect(drafts.retained()).toEqual([reply]);
    end();
    expect(drafts.retained()).toEqual([]);
  });
  it("cannot clear or submit an in-progress IME composition", async () => {
    const f = fixture();
    const pending = f.drafts.submit(f.draft);
    const end = f.drafts.beginComposition(f.draft);
    f.resolve({ ok: true });
    await pending;
    expect(f.drafts.retained()).toEqual([f.draft]);
    expect(f.draft.state().body).toBe(" original ");
    expect(await f.drafts.submit(f.draft)).toBe(false);
    f.drafts.write(f.draft, "完成");
    end();
    end();
    expect(f.draft.state()).toMatchObject({ body: "完成", composing: 0 });
  });
  it("retains explicit new composers, not empty reply fields on every thread", () => {
    const store = new ReviewCommentDrafts(vi.fn(), () => "");
    const reply = store.open(file, { kind: "reply", rootId: 10 });
    const second = store.open(file, { kind: "reply", rootId: 11 });
    expect(store.retained()).toEqual([]);
    const firstNew = store.open(file, { kind: "new", anchor });
    const secondNew = store.open(file, { kind: "new", anchor });
    store.write(reply, "reply to first thread");
    expect(store.retained()).toEqual([reply, firstNew, secondNew]);
    expect(store.open(file, { kind: "reply", rootId: 10 })).toBe(reply);
    expect(store.open(file, { kind: "reply", rootId: 11 })).toBe(second);
    expect(store.open({ ...file, number: 13 }, { kind: "reply", rootId: 10 })).not.toBe(reply);
  });

  it("clears only after acknowledgement, while suppressing duplicate submission", async () => {
    const f = fixture();
    const pending = f.drafts.submit(f.draft);
    expect(f.draft.state()).toMatchObject({ body: " original ", pending: true });
    expect(await f.drafts.submit(f.draft)).toBe(false);
    expect(f.post).toHaveBeenCalledExactlyOnceWith({
      ...file,
      line: 8,
      inReplyTo: 0,
      side: "right",
      body: "original",
    });
    expect(() => f.drafts.discard(f.draft)).toThrow("still being submitted");
    f.resolve({ ok: true });
    expect(await pending).toBe(true);
    expect(f.drafts.retained()).toEqual([]);
  });

  it("does not erase typing made while a post is in flight", async () => {
    const f = fixture();
    const pending = f.drafts.submit(f.draft);
    f.drafts.write(f.draft, "additional thought");
    f.resolve({ ok: true });
    await pending;
    expect(f.drafts.retained()).toEqual([f.draft]);
    expect(f.draft.state()).toMatchObject({
      body: "additional thought",
      pending: false,
      error: "",
      message: "Comment posted.",
    });
  });

  it("preserves text and surfaces a failed host response", async () => {
    const f = fixture();
    const pending = f.drafts.submit(f.draft);
    f.resolve({ ok: false, error: "PR is no longer active" });
    expect(await pending).toBe(false);
    expect(f.draft.state()).toMatchObject({
      body: " original ",
      pending: false,
      error: "PR is no longer active",
    });
  });

  it("does not silently retry an ambiguous transport failure", async () => {
    const f = fixture();
    const pending = f.drafts.submit(f.draft);
    f.reject(new Error("Connection closed before acknowledgement"));
    expect(await pending).toBe(false);
    expect(f.draft.state().body).toBe(" original ");
    expect(f.draft.state().error).toContain("before acknowledgement");
    expect(f.post).toHaveBeenCalledTimes(1);
  });

  it("keeps the exact thread target when two threads share a line", async () => {
    const f = fixture();
    const reply = f.drafts.open(file, { kind: "reply", rootId: 21 });
    f.drafts.open(file, { kind: "reply", rootId: 22 });
    f.drafts.write(reply, "answer");
    const pending = f.drafts.submit(reply);
    expect(f.post).toHaveBeenCalledExactlyOnceWith({
      ...file,
      line: 0,
      inReplyTo: 21,
      side: "right",
      body: "answer",
    });
    f.resolve({ ok: true });
    await pending;
    expect(reply.state().body).toBe("");
    expect(f.drafts.retained()).toEqual([f.draft]);
    expect(f.drafts.open(file, { kind: "reply", rootId: 21 })).toBe(reply);
  });

  it("keeps removed or stale-anchor drafts reachable but prevents silent posting", async () => {
    const f = fixture();
    f.validate.mockReturnValue("The file changed; select a new comment location.");
    f.drafts.refreshValidity();
    expect(await f.drafts.submit(f.draft)).toBe(false);
    expect(f.post).not.toHaveBeenCalled();
    expect(f.drafts.retained()).toEqual([f.draft]);
    expect(f.draft.state()).toMatchObject({
      body: " original ",
      error: expect.stringContaining("file changed"),
    });
    f.drafts.write(f.draft, "still editable");
    expect(f.draft.state().unavailable).toContain("file changed");
  });

  it("rejects cross-session records and late callbacks after exact owner disposal", async () => {
    const f = fixture();
    const other = new ReviewCommentDrafts(vi.fn(), () => "");
    expect(() => other.write(f.draft, "wrong session")).toThrow("live session");
    await expect(other.submit(f.draft)).rejects.toThrow("live session");
    const pending = f.drafts.submit(f.draft);
    f.drafts.dispose();
    f.resolve({ ok: true });
    await pending;
    expect(f.drafts.retained()).toEqual([]);
    expect(() => f.drafts.open(file, { kind: "new", anchor })).toThrow("session is closed");
    expect(() => f.drafts.write(f.draft, "resurrect")).toThrow("live session");
  });
});
