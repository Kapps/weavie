import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Capture the store's host listener + every outbound message; the bridge itself is window-coupled.
const posted = vi.hoisted(() => [] as Array<Record<string, unknown>>);
const hostHandlers = vi.hoisted(() => [] as Array<(m: unknown) => void>);
vi.mock("../../bridge", () => ({
  onHostMessage: (h: (m: unknown) => void) => {
    hostHandlers.push(h);
    return () => {};
  },
  postToEditorBackend: (m: Record<string, unknown>) => {
    posted.push(m);
  },
}));

const store = await import("./media-store");
const { MAX_MEDIA_BYTES } = await import("./media-types");

const push = (message: Record<string, unknown>): void => {
  for (const h of hostHandlers) {
    h(message);
  }
};

const lastPosted = (): Record<string, unknown> =>
  posted[posted.length - 1] as Record<string, unknown>;

// Answer the pending fs-stat, then the fs-read-bytes, driving `path` to ready. "aGk=" is "hi".
const serve = (size: number, dataB64 = "aGk="): void => {
  const stat = lastPosted();
  push({ type: "fs-stat-result", id: stat.id, ok: true, exists: true, size });
  if (size <= MAX_MEDIA_BYTES) {
    const read = lastPosted();
    expect(read.type).toBe("fs-read-bytes");
    push({ type: "fs-read-bytes-result", id: read.id, ok: true, dataB64 });
  }
};

let urlSeq = 0;
const revoked: string[] = [];

beforeEach(() => {
  posted.length = 0;
  revoked.length = 0;
  // jsdom lacks createObjectURL; a counting stub also lets revocation be asserted.
  URL.createObjectURL = () => `blob:test-${++urlSeq}`;
  URL.revokeObjectURL = (url: string) => {
    revoked.push(url);
  };
});

afterEach(() => {
  for (const path of ["/ws/a.png", "/ws/b.png"]) {
    store.releaseMedia(path);
  }
});

describe("media-store", () => {
  it("stats first, then reads bytes and exposes a blob URL", () => {
    store.loadMedia("/ws/a.png");
    expect(store.mediaDoc("/ws/a.png")?.status).toBe("loading");
    expect(lastPosted().type).toBe("fs-stat");

    serve(2);

    const doc = store.mediaDoc("/ws/a.png");
    expect(doc?.status).toBe("ready");
    expect(doc?.url).toMatch(/^blob:/);
  });

  it("refuses an over-bound file loudly, without requesting its bytes", () => {
    store.loadMedia("/ws/a.png");
    serve(MAX_MEDIA_BYTES + 1);

    const doc = store.mediaDoc("/ws/a.png");
    expect(doc?.status).toBe("error");
    expect(doc?.error).toContain("too large");
    expect(posted.filter((m) => m.type === "fs-read-bytes")).toHaveLength(0);
  });

  it("re-fetches on fs-change, holding the old frame until the new bytes land, then revokes it", () => {
    store.loadMedia("/ws/a.png");
    serve(2);
    const first = store.mediaDoc("/ws/a.png")?.url;

    push({ type: "fs-change", changes: [{ path: "/ws/a.png", kind: "updated" }] });
    expect(store.mediaDoc("/ws/a.png")).toEqual({ status: "loading", url: first });

    serve(2);
    expect(store.mediaDoc("/ws/a.png")?.status).toBe("ready");
    expect(store.mediaDoc("/ws/a.png")?.url).not.toBe(first);
    expect(revoked).toContain(first);
  });

  it("turns a deletion into an in-pane error and revokes the URL", () => {
    store.loadMedia("/ws/a.png");
    serve(2);
    const url = store.mediaDoc("/ws/a.png")?.url as string;

    push({ type: "fs-change", changes: [{ path: "/ws/a.png", kind: "deleted" }] });

    expect(store.mediaDoc("/ws/a.png")?.status).toBe("error");
    expect(store.mediaDoc("/ws/a.png")?.error).toContain("deleted");
    expect(revoked).toContain(url);
  });

  it("release drops the entry, revokes the URL, and discards a late reply", () => {
    store.loadMedia("/ws/a.png");
    serve(2);
    const url = store.mediaDoc("/ws/a.png")?.url as string;
    store.releaseMedia("/ws/a.png");
    expect(store.mediaDoc("/ws/a.png")).toBeUndefined();
    expect(revoked).toContain(url);

    store.loadMedia("/ws/b.png");
    const stat = lastPosted();
    store.releaseMedia("/ws/b.png");
    push({ type: "fs-stat-result", id: stat.id, ok: true, exists: true, size: 2 });
    expect(store.mediaDoc("/ws/b.png")).toBeUndefined(); // stale reply after release: no resurrection
  });

  it("surfaces a stat failure loudly instead of calling the file missing", () => {
    store.loadMedia("/ws/a.png");
    const stat = lastPosted();
    push({ type: "fs-stat-result", id: stat.id, ok: false, exists: false, size: 0, error: "boom" });
    expect(store.mediaDoc("/ws/a.png")).toEqual({ status: "error", error: "boom" });
  });

  it("ignores fs-stat-result traffic belonging to the file provider's ids", () => {
    store.loadMedia("/ws/a.png");
    push({ type: "fs-stat-result", id: "fs1", ok: true, exists: true, size: 2 });
    expect(store.mediaDoc("/ws/a.png")?.status).toBe("loading"); // foreign id: untouched
  });
});
