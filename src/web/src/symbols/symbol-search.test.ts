import { describe, expect, it, vi } from "vitest";
import type { FlatSymbol, SymbolActions, SymbolQueryResult } from "./symbol-match";

vi.mock("../bridge", () => ({ log: vi.fn() }));
vi.mock(
  "solid-js",
  async () => await vi.importActual<typeof import("solid-js")>("solid-js/dist/solid.js"),
);

const { createRoot, createSignal } = await import("solid-js");
const { createSymbolSearch, settleSymbolQuery } = await import("./symbol-search");

const alpha: FlatSymbol = {
  name: "alpha",
  kind: "function",
  container: "",
  path: "/workspace/alpha.ts",
  range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 6 },
};

describe("settleSymbolQuery", () => {
  it("settles a rejection without reporting it after the query becomes obsolete", async () => {
    const query = Promise.withResolvers<string>();
    const resolved = vi.fn();
    const rejected = vi.fn();
    let obsolete = false;
    settleSymbolQuery(query.promise, () => obsolete, resolved, rejected);

    obsolete = true;
    query.reject(Object.assign(new Error("Canceled"), { name: "Canceled" }));
    await Promise.resolve();

    expect(resolved).not.toHaveBeenCalled();
    expect(rejected).not.toHaveBeenCalled();
  });

  it("clears prior workspace rows when the current query fails", async () => {
    vi.useFakeTimers();
    const [query, setQuery] = createSignal("alpha");
    const success: SymbolQueryResult = { providerAvailable: true, items: [alpha] };
    const symbols = {
      documentSymbols: vi.fn(),
      workspaceSymbols: vi.fn((value: string) =>
        value === "alpha" ? Promise.resolve(success) : Promise.reject(new Error("LSP failed")),
      ),
      preview: vi.fn(),
      cancelPreview: vi.fn(),
      commitPreview: vi.fn(),
    } satisfies SymbolActions;

    let dispose = (): void => undefined;
    const search = createRoot((rootDispose) => {
      dispose = rootDispose;
      return createSymbolSearch({
        active: () => "wsSymbol",
        query,
        reloadKey: () => 0,
        symbols,
      });
    });

    try {
      await vi.advanceTimersByTimeAsync(150);
      expect(search.status()).toBe("ready");
      expect(search.view().map((item) => item.sym.name)).toEqual(["alpha"]);

      setQuery("beta");
      await vi.advanceTimersByTimeAsync(150);
      expect(search.status()).toBe("error");
      expect(search.view()).toEqual([]);
    } finally {
      dispose();
      vi.useRealTimers();
    }
  });
});
