import { describe, expect, it } from "vitest";
import { makeTransform, transformHex } from "./colors";
import { type OverrideOp, resolveTheme } from "./overrides";
import type { VsCodeColorTheme } from "./vscode-theme";

function base(): VsCodeColorTheme {
  return {
    name: "test",
    type: "dark",
    colors: { "editor.background": "#202020", "editor.foreground": "#e0e0e0" },
    tokenColors: [{ name: "comment", scope: "comment", settings: { foreground: "#808080" } }],
    semanticTokenColors: { variable: "#c0c0c0", "class.declaration": { foreground: "#a0a0a0" } },
  };
}

describe("spellcheck theme color", () => {
  it.each([
    ["dark", "#70b7ff"],
    ["hc", "#70b7ff"],
    ["light", "#005fb8"],
    ["hcLight", "#005fb8"],
  ] as const)("supplies an opaque default for a %s theme without the key", (type, color) => {
    expect(resolveTheme({ ...base(), type }, []).colors["editorSpellCheck.foreground"]).toBe(color);
  });

  it("layers theme colors and ordered overrides over the default without retaining removed overrides", () => {
    const theme = base();
    theme.colors["editorSpellCheck.foreground"] = "#ff8800";
    const ops: OverrideOp[] = [
      { kind: "transform", op: "darken", amount: 0.2, target: "colors" },
      { kind: "set", key: "editorSpellCheck.foreground", value: "#00ffff" },
    ];
    expect(resolveTheme(theme, ops).colors["editorSpellCheck.foreground"]).toBe("#00ffff");
    expect(resolveTheme(theme, ops.slice(0, 1)).colors["editorSpellCheck.foreground"]).toBe(
      transformHex("#ff8800", makeTransform("darken", 0.2)),
    );
    expect(resolveTheme(theme, []).colors["editorSpellCheck.foreground"]).toBe("#ff8800");
    expect(resolveTheme(base(), []).colors["editorSpellCheck.foreground"]).toBe("#70b7ff");
  });

  it("includes the default in palette transforms", () => {
    const ops: OverrideOp[] = [{ kind: "transform", op: "darken", amount: 0.2, target: "colors" }];
    expect(resolveTheme(base(), ops).colors["editorSpellCheck.foreground"]).toBe(
      transformHex("#70b7ff", makeTransform("darken", 0.2)),
    );
  });
});

describe("resolveTheme set ops", () => {
  it("writes a workbench colour by key", () => {
    const ops: OverrideOp[] = [{ kind: "set", key: "editor.background", value: "#000000" }];
    expect(resolveTheme(base(), ops).colors["editor.background"]).toBe("#000000");
  });

  it("a fontStyle-only set on the colors table is a no-op (workbench colours carry no style)", () => {
    const ops: OverrideOp[] = [{ kind: "set", key: "editor.background", fontStyle: "italic" }];
    expect(resolveTheme(base(), ops).colors["editor.background"]).toBe("#202020");
  });

  it("appends a last-wins tokenColors rule for a scope", () => {
    const ops: OverrideOp[] = [
      { kind: "set", table: "tokenColors", key: "keyword", value: "#ff0000", fontStyle: "bold" },
    ];
    const { tokenColors } = resolveTheme(base(), ops);
    const added = tokenColors[tokenColors.length - 1];
    expect(added).toMatchObject({
      scope: "keyword",
      settings: { foreground: "#ff0000", fontStyle: "bold" },
    });
  });

  it("merges a style-only semantic set over the existing foreground", () => {
    const ops: OverrideOp[] = [
      { kind: "set", table: "semanticTokenColors", key: "variable", fontStyle: "underline" },
    ];
    expect(resolveTheme(base(), ops).semanticTokenColors.variable).toEqual({
      foreground: "#c0c0c0",
      fontStyle: "underline",
    });
  });

  it("keeps the bare-hex form for a colour-only semantic set", () => {
    const ops: OverrideOp[] = [
      { kind: "set", table: "semanticTokenColors", key: "variable", value: "#111111" },
    ];
    expect(resolveTheme(base(), ops).semanticTokenColors.variable).toBe("#111111");
  });
});

describe("resolveTheme transform ops", () => {
  it("darkens only the colors table when target is 'colors'", () => {
    const ops: OverrideOp[] = [{ kind: "transform", op: "darken", amount: 1, target: "colors" }];
    const out = resolveTheme(base(), ops);
    expect(out.colors["editor.background"]).toBe("#000000");
    // Syntax tables untouched.
    expect(out.tokenColors[0]?.settings.foreground).toBe("#808080");
  });

  it("'syntax' sweeps both syntax tables but leaves workbench colours alone", () => {
    const ops: OverrideOp[] = [{ kind: "transform", op: "darken", amount: 1, target: "syntax" }];
    const out = resolveTheme(base(), ops);
    expect(out.colors["editor.background"]).toBe("#202020");
    expect(out.tokenColors[0]?.settings.foreground).toBe("#000000");
    expect(out.semanticTokenColors.variable).toBe("#000000");
  });

  it("defaults the target to 'all'", () => {
    const ops: OverrideOp[] = [{ kind: "transform", op: "darken", amount: 1 }];
    const out = resolveTheme(base(), ops);
    expect(out.colors["editor.background"]).toBe("#000000");
    expect(out.tokenColors[0]?.settings.foreground).toBe("#000000");
  });

  it("applies ops in order so a later set wins over an earlier transform", () => {
    const ops: OverrideOp[] = [
      { kind: "transform", op: "darken", amount: 1 },
      { kind: "set", key: "editor.background", value: "#123456" },
    ];
    expect(resolveTheme(base(), ops).colors["editor.background"]).toBe("#123456");
  });
});

describe("resolveTheme purity", () => {
  it("never mutates the base theme", () => {
    const original = base();
    resolveTheme(original, [
      { kind: "set", key: "editor.background", value: "#000000" },
      { kind: "transform", op: "lighten", amount: 1 },
    ]);
    expect(original.colors["editor.background"]).toBe("#202020");
    expect(original.tokenColors[0]?.settings.foreground).toBe("#808080");
  });
});
