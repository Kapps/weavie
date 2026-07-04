import { describe, expect, it, vi } from "vitest";

// keybindings.ts pulls in the registry (and through it the window-coupled bridge) only for the resolver;
// formatKey itself needs none of it. Stub the registry so the module loads in the pure node env.
vi.mock("./registry", () => ({
  getKeybindings: () => [],
  onCommandsChanged: () => () => {},
  runForKeybinding: () => false,
}));

const { formatKey } = await import("./keybindings");

// In the node test env navigator is non-mac, so $mod renders as "Ctrl".
describe("formatKey (non-mac)", () => {
  it("renders $mod as Ctrl and uppercases a single-letter key", () => {
    expect(formatKey("$mod+Shift+p")).toBe("Ctrl+Shift+P");
  });

  it("normalises the control and mod aliases to Ctrl", () => {
    expect(formatKey("control+k")).toBe("Ctrl+K");
    expect(formatKey("mod+a")).toBe("Ctrl+A");
  });

  it("title-cases multi-character key names", () => {
    expect(formatKey("$mod+up")).toBe("Ctrl+Up");
    expect(formatKey("alt+enter")).toBe("Alt+Enter");
  });

  it("collapses ctrl+$mod to a single Ctrl where $mod is Ctrl", () => {
    expect(formatKey("ctrl+$mod+Right")).toBe("Ctrl+Right");
  });

  it("renders the mouse-button tokens canonically regardless of spelling", () => {
    expect(formatKey("MouseBack")).toBe("MouseBack");
    expect(formatKey("mouseforward")).toBe("MouseForward");
  });
});
