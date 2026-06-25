import { describe, expect, it } from "vitest";
import { isPreviewMode, toggleViewMode } from "./view-mode-store";

describe("view-mode-store", () => {
  it("defaults a never-seen file to source", () => {
    expect(isPreviewMode("c:/fresh.md")).toBe(false);
  });

  it("toggles between source and preview, returning the new mode", () => {
    const path = "c:/toggle.md";
    expect(toggleViewMode(path)).toBe("preview");
    expect(isPreviewMode(path)).toBe(true);
    expect(toggleViewMode(path)).toBe("source");
    expect(isPreviewMode(path)).toBe(false);
  });

  it("keys by canonical fs-path so drive-letter casing doesn't split the entry", () => {
    toggleViewMode("C:/Casing.md"); // -> preview, stored canonically (lowercased drive)
    expect(isPreviewMode("c:/Casing.md")).toBe(true);
    // Toggling via the other casing flips the same entry back off.
    expect(toggleViewMode("c:/Casing.md")).toBe("source");
    expect(isPreviewMode("C:/Casing.md")).toBe(false);
  });
});
