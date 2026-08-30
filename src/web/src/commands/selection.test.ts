import { afterEach, describe, expect, it } from "vitest";
import { noteSelectionChange, registerSelectionSource, selectedText } from "./selection";

// The registry is module-global; each test registers its own sources and deregisters them after.
const cleanups: (() => void)[] = [];

function source(key: string, text: () => string): void {
  cleanups.push(registerSelectionSource(key, text));
  noteSelectionChange(key);
}

afterEach(() => {
  for (const off of cleanups.splice(0)) {
    off();
  }
});

describe("selectedText", () => {
  it("returns null when nothing is highlighted", () => {
    source("editor", () => "");
    expect(selectedText()).toBeNull();
  });

  it("reads the highlight, trimmed", () => {
    source("editor", () => "  greet  ");
    expect(selectedText()).toBe("greet");
  });

  it("takes the most recently changed source, not the pane that was highlighted first", () => {
    source("editor", () => "greet");
    source("document", () => "farewell");
    expect(selectedText()).toBe("farewell");
    noteSelectionChange("editor");
    expect(selectedText()).toBe("greet");
  });

  it("skips a source whose selection has since been cleared", () => {
    source("editor", () => "greet");
    source("document", () => "");
    expect(selectedText()).toBe("greet");
  });

  it("returns null for a multi-line highlight rather than an older pane's selection", () => {
    source("editor", () => "greet");
    source("document", () => "greet\nfarewell");
    expect(selectedText()).toBeNull();
  });

  it("forgets a deregistered source", () => {
    source("editor", () => "greet");
    for (const off of cleanups.splice(0)) {
      off();
    }
    expect(selectedText()).toBeNull();
  });

  it("keeps the live reader when a pane remounts under the same key before the old one tears down", () => {
    const offOld = registerSelectionSource("terminal", () => "stale");
    source("terminal", () => "greet");
    offOld();
    expect(selectedText()).toBe("greet");
  });
});
