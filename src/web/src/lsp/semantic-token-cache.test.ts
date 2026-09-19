import {
  CancellationToken,
  CancellationTokenSource,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/cancellation";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Range, SemanticTokens, TextDocument } from "vscode";
import { SemanticTokenCache } from "./semantic-token-cache";

const caches: SemanticTokenCache[] = [];
afterEach(() => {
  for (const cache of caches.splice(0)) cache.dispose();
});
const tokens = (resultId: string): SemanticTokens => ({
  data: new Uint32Array([0, 1, 3, 0, 1]),
  resultId,
});
function fixture() {
  const document = { version: 1, languageId: "typescript" } as TextDocument;
  const full = vi.fn().mockResolvedValue(tokens("first"));
  const edits = vi.fn().mockResolvedValue({
    edits: [{ start: 1, deleteCount: 1, data: new Uint32Array([2]) }],
    resultId: "second",
  });
  const range = vi.fn().mockResolvedValue(tokens("range"));
  const cache = new SemanticTokenCache({
    full: { provideDocumentSemanticTokens: full, provideDocumentSemanticTokensEdits: edits },
    range: { provideDocumentRangeSemanticTokens: range },
  });
  caches.push(cache);
  return { document, full, edits, range, cache };
}

describe("semantic request ownership", () => {
  it("shares pending and completed snapshots across highlighting, spelling, and remounts", async () => {
    const { cache, document, full } = fixture();
    const pending = Promise.withResolvers<SemanticTokens>();
    full.mockReturnValue(pending.promise);
    const highlighting = cache.read(document, CancellationToken.None);
    const spelling = cache.read(document, CancellationToken.None);
    pending.resolve(tokens("first"));
    expect(await highlighting).toEqual(await spelling);
    await cache.read(document, CancellationToken.None);
    expect(full).toHaveBeenCalledTimes(1);
  });

  it("isolates a departing consumer's cancellation from another consumer", async () => {
    const { cache, document, full } = fixture();
    const pending = Promise.withResolvers<SemanticTokens>();
    full.mockReturnValue(pending.promise);
    const consumer = new CancellationTokenSource();
    const leaving = cache.read(document, consumer.token);
    const staying = cache.read(document, CancellationToken.None);
    consumer.dispose(true);
    expect(await leaving).toBeNull();
    pending.resolve(tokens("first"));
    expect(await staying).toEqual(tokens("first"));
    expect(full).toHaveBeenCalledTimes(1);
    expect(full.mock.calls[0]![1].isCancellationRequested).toBe(false);
  });

  it("uses one delta request after an edit and returns a full snapshot to both consumers", async () => {
    const { cache, document, edits } = fixture();
    await cache.read(document, CancellationToken.None);
    Object.assign(document, { version: 2 });
    const results = await Promise.all([
      cache.read(document, CancellationToken.None),
      cache.read(document, CancellationToken.None),
    ]);
    expect(edits).toHaveBeenCalledTimes(1);
    expect(edits.mock.calls[0]![1]).toBe("first");
    expect(results[0]!.data).toEqual(new Uint32Array([0, 2, 3, 0, 1]));
    expect(results[1]).toEqual(results[0]);
    cache.refresh();
    await cache.read(document, CancellationToken.None);
    expect(edits).toHaveBeenCalledTimes(2);
    expect(edits.mock.calls[1]![1]).toBe("second");
  });

  it("discards a late response after invalidation without replacing newer cached tokens", async () => {
    const { cache, document, full } = fixture();
    const pending = Promise.withResolvers<SemanticTokens>();
    full.mockReturnValueOnce(pending.promise);
    const old = cache.read(document, CancellationToken.None);
    await Promise.resolve();
    cache.invalidate(document);
    const fresh = await cache.read(document, CancellationToken.None);
    pending.resolve(tokens("stale"));
    expect(await old).toBeNull();
    expect(await cache.read(document, CancellationToken.None)).toEqual(fresh);
    expect(full).toHaveBeenCalledTimes(2);
  });

  it("evicts failures and empty responses, and releases closed documents and changed languages", async () => {
    const { cache, document, full } = fixture();
    full.mockRejectedValueOnce(new Error("server failed")).mockResolvedValueOnce(null);
    await expect(cache.read(document, CancellationToken.None)).rejects.toThrow("server failed");
    expect(await cache.read(document, CancellationToken.None)).toBeNull();
    await cache.read(document, CancellationToken.None);
    cache.close(document);
    await cache.read(document, CancellationToken.None);
    Object.assign(document, { languageId: "javascript" });
    await cache.read(document, CancellationToken.None);
    expect(full).toHaveBeenCalledTimes(5);
  });

  it("shares only identical ranges for range-only requests and invalidates on refresh", async () => {
    const { cache, document, range } = fixture();
    const first = { start: { line: 0, character: 0 }, end: { line: 10, character: 0 } } as Range;
    const second = { ...first, end: { line: 20, character: 0 } } as Range;
    await Promise.all([
      cache.readRange(document, first, CancellationToken.None),
      cache.readRange(document, first, CancellationToken.None),
    ]);
    await cache.readRange(document, second, CancellationToken.None);
    expect(range).toHaveBeenCalledTimes(2);
    cache.refresh();
    await cache.readRange(document, first, CancellationToken.None);
    expect(range).toHaveBeenCalledTimes(3);
  });

  it("keeps separate document lifetimes isolated and does not fetch for cancelled consumers", async () => {
    const { cache, document, full } = fixture();
    await cache.read(document, CancellationToken.Cancelled);
    expect(full).not.toHaveBeenCalled();
    await cache.read(document, CancellationToken.None);
    await cache.read({ ...document } as TextDocument, CancellationToken.None);
    expect(full).toHaveBeenCalledTimes(2);
  });

  it("applies large delta payloads without spreading them onto the call stack", async () => {
    const { cache, document, edits } = fixture();
    await cache.read(document, CancellationToken.None);
    const data = new Uint32Array(250_000).fill(7);
    edits.mockResolvedValue({ edits: [{ start: 0, deleteCount: 5, data }], resultId: "large" });
    cache.refresh();
    expect((await cache.read(document, CancellationToken.None))!.data).toEqual(data);
  });
});
