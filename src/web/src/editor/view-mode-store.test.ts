import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const options = vi.hoisted(() => ({
  markdownView: "source",
  changed: (_options: { markdownView: string }) => {},
}));
vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));
vi.mock("../editor-options", () => ({
  currentEditorOptions: () => options,
  onEditorOptionsChanged: (handler: typeof options.changed) => {
    options.changed = handler;
    return () => {};
  },
}));
const { isPreviewMode, setViewMode, toggleViewMode } = await import("./view-mode-store");
const owner = {} as ClientSession;

describe("view-mode-store", () => {
  it("defaults a never-seen file to source", () => {
    expect(isPreviewMode(owner, "c:/fresh.md")).toBe(false);
  });

  it("follows editor.markdownView for Markdown only, until a file's mode is chosen", () => {
    options.changed({ markdownView: "preview" });
    expect(isPreviewMode(owner, "c:/default.md")).toBe(true);
    expect(isPreviewMode(owner, "c:/default.svg")).toBe(false);
    expect(toggleViewMode(owner, "c:/default.md")).toBe("source");
    expect(isPreviewMode(owner, "c:/default.md")).toBe(false);
    options.changed({ markdownView: "source" });
  });

  it("toggles between source and preview, returning the new mode", () => {
    const path = "c:/toggle.md";
    expect(toggleViewMode(owner, path)).toBe("preview");
    expect(isPreviewMode(owner, path)).toBe(true);
    expect(toggleViewMode(owner, path)).toBe("source");
    expect(isPreviewMode(owner, path)).toBe(false);
  });

  it("setting preview stays in preview when already there", () => {
    setViewMode(owner, "c:/shown.md", "preview");
    setViewMode(owner, "c:/shown.md", "preview");
    expect(isPreviewMode(owner, "c:/shown.md")).toBe(true);
  });

  it("keys by canonical fs-path so drive-letter casing doesn't split the entry", () => {
    toggleViewMode(owner, "C:/Casing.md"); // -> preview, stored canonically (lowercased drive)
    expect(isPreviewMode(owner, "c:/Casing.md")).toBe(true);
    // Toggling via the other casing flips the same entry back off.
    expect(toggleViewMode(owner, "c:/Casing.md")).toBe("source");
    expect(isPreviewMode(owner, "C:/Casing.md")).toBe(false);
  });
});

it("does not share preview mode between sessions with the same path", () => {
  const other = {} as ClientSession;
  toggleViewMode(owner, "/same.md");
  expect(isPreviewMode(other, "/same.md")).toBe(false);
});
