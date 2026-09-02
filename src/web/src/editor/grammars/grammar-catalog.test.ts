import { describe, expect, it } from "vitest";
import { buildBroadCatalog } from "./grammar-catalog";

describe("buildBroadCatalog", () => {
  it("keeps shared Python-scope extensions on the curated Python language", () => {
    const catalog = buildBroadCatalog(new Set([".py", ".pyi", ".rs"]));
    const python = catalog.find((entry) => entry.scopeName === "source.python");

    expect(python).toMatchObject({ languageId: "python", registerGrammar: false });
    expect(python?.extensions).toContain(".bzl");
    expect(python?.extensions).not.toContain(".py");
    expect(catalog.find((entry) => entry.scopeName === "source.rust")).toMatchObject({
      languageId: "rust",
      extensions: [".rs.in"],
      registerGrammar: false,
    });
  });

  it("never reclaims an extension already registered with Monaco", () => {
    const claimed = new Set([".py", ".rs", ".css"]);
    const catalog = buildBroadCatalog(claimed);
    const registered = catalog.flatMap((entry) => entry.extensions);

    for (const extension of claimed) {
      expect(registered).not.toContain(extension);
    }
  });

  it("registers non-curated languages with their bundled grammar", () => {
    const css = buildBroadCatalog(new Set()).find((entry) => entry.scopeName === "source.css");

    expect(css).toMatchObject({ languageId: "css", registerGrammar: true });
  });
});
