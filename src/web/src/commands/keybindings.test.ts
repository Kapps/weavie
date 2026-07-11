import { describe, expect, it, vi } from "vitest";

const commandState = vi.hoisted(() => ({
  bindings: [] as Array<{ key: string; command: string }>,
  run: vi.fn(() => true),
}));

// keybindings.ts pulls in the registry (and through it the window-coupled bridge) only for the resolver;
// formatKey itself needs none of it. Stub the registry so the module loads in the pure node env.
vi.mock("./registry", () => ({
  getKeybindings: () => commandState.bindings,
  onCommandsChanged: () => () => {},
  runForKeybinding: commandState.run,
}));

const { formatKey, installKeybindings } = await import("./keybindings");

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

describe("keyboard resolver", () => {
  it("does not run Enter bindings while an IME composition is active", () => {
    commandState.bindings = [{ key: "enter", command: "weavie.agent.submit" }];
    commandState.run.mockClear();
    let keydown: ((event: KeyboardEvent) => void) | undefined;
    vi.stubGlobal("window", {
      addEventListener: (type: string, handler: (event: KeyboardEvent) => void) => {
        if (type === "keydown") {
          keydown = handler;
        }
      },
      removeEventListener: vi.fn(),
    });
    const dispose = installKeybindings();

    keydown?.({
      key: "Enter",
      isComposing: true,
      ctrlKey: false,
      metaKey: false,
      shiftKey: false,
      altKey: false,
    } as KeyboardEvent);

    expect(commandState.run).not.toHaveBeenCalled();
    dispose();
    vi.unstubAllGlobals();
  });
});
