import { describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));
const { isPreviewMode, toggleViewMode } = await import("./view-mode-store");
const owner = {} as ClientSession;

describe("view-mode-store", () => {
  it("defaults a never-seen file to source", () => {
    expect(isPreviewMode(owner, "c:/fresh.md")).toBe(false);
  });

  it("toggles between source and preview, returning the new mode", () => {
    const path = "c:/toggle.md";
    expect(toggleViewMode(owner, path)).toBe("preview");
    expect(isPreviewMode(owner, path)).toBe(true);
    expect(toggleViewMode(owner, path)).toBe("source");
    expect(isPreviewMode(owner, path)).toBe(false);
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
