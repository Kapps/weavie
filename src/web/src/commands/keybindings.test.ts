import { beforeEach, describe, expect, it, vi } from "vitest";
import { setContext } from "./context";
import type { ResolvedKeybinding } from "./types";

const commandState = vi.hoisted(() => ({
  entries: [] as Array<{ catalogBackendId: string; binding: ResolvedKeybinding }>,
  run: vi.fn(() => true),
}));

// keybindings.ts pulls in the registry (and through it the window-coupled bridge) only for the resolver;
// formatKey itself needs none of it. Stub the registry so the module loads in the pure node env.
vi.mock("./registry", () => ({
  getActiveKeybindingEntries: () => commandState.entries,
  onCommandsChanged: () => () => {},
  runForKeybindingFromCatalog: commandState.run,
}));

const { formatKey, installKeybindings } = await import("./keybindings");

beforeEach(() => {
  commandState.entries = [];
  commandState.run.mockClear();
  setContext("nativeShell", true);
  setContext("browserShell", false);
  setContext("modalOpen", false);
  setContext("newSessionPromptFocused", false);
});

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
  it("normalizes GTK's ISO_Left_Tab key for Ctrl+Shift+Tab bindings", () => {
    commandState.entries = [
      {
        catalogBackendId: "local",
        binding: { key: "ctrl+shift+tab", command: "weavie.session.prev", args: undefined },
      },
    ];
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
    const preventDefault = vi.fn();
    const stopPropagation = vi.fn();

    keydown?.({
      key: "ISO_Left_Tab",
      isComposing: false,
      ctrlKey: true,
      metaKey: false,
      shiftKey: true,
      altKey: false,
      preventDefault,
      stopPropagation,
    } as unknown as KeyboardEvent);

    expect(commandState.run).toHaveBeenCalledWith("local", "weavie.session.prev", undefined);
    expect(preventDefault).toHaveBeenCalledOnce();
    expect(stopPropagation).toHaveBeenCalledOnce();
    dispose();
    vi.unstubAllGlobals();
  });

  it("does not run Enter bindings while an IME composition is active", () => {
    commandState.entries = [
      {
        catalogBackendId: "local",
        binding: { key: "enter", command: "weavie.agent.submit", args: undefined },
      },
    ];
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

  it("does not route shortcuts to the session behind a modal", () => {
    commandState.entries = [
      {
        catalogBackendId: "local",
        binding: { key: "ctrl+1", command: "weavie.pane.focusByIndex", args: { index: 1 } },
      },
    ];
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
    setContext("modalOpen", true);

    keydown?.({
      key: "1",
      isComposing: false,
      ctrlKey: true,
      metaKey: false,
      shiftKey: false,
      altKey: false,
    } as KeyboardEvent);

    expect(commandState.run).not.toHaveBeenCalled();
    dispose();
    vi.unstubAllGlobals();
  });

  it("routes bindings that are explicitly scoped to the active modal", () => {
    commandState.entries = [
      {
        catalogBackendId: "local",
        binding: {
          key: "shift+enter",
          command: "weavie.session.submitNew",
          when: "newSessionPromptFocused",
          activeInModal: true,
        },
      },
    ];
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
    setContext("modalOpen", true);
    setContext("newSessionPromptFocused", true);

    keydown?.({
      key: "Enter",
      isComposing: false,
      ctrlKey: false,
      metaKey: false,
      shiftKey: true,
      altKey: false,
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
    } as unknown as KeyboardEvent);

    expect(commandState.run).toHaveBeenCalledWith("local", "weavie.session.submitNew", undefined);
    dispose();
    vi.unstubAllGlobals();
  });

  it("routes new-session paste only from the focused native modal prompt", () => {
    commandState.entries = [
      {
        catalogBackendId: "local",
        binding: {
          key: "ctrl+v",
          command: "weavie.session.pasteNew",
          when: "newSessionPromptFocused && !browserShell",
          activeInModal: true,
        },
      },
    ];
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
    setContext("modalOpen", true);
    setContext("newSessionPromptFocused", true);
    const paste = (): void =>
      keydown?.({
        key: "v",
        isComposing: false,
        ctrlKey: true,
        metaKey: false,
        shiftKey: false,
        altKey: false,
        preventDefault: vi.fn(),
        stopPropagation: vi.fn(),
      } as unknown as KeyboardEvent);

    paste();
    expect(commandState.run).toHaveBeenCalledWith("local", "weavie.session.pasteNew", undefined);

    commandState.run.mockClear();
    setContext("browserShell", true);
    paste();
    expect(commandState.run).not.toHaveBeenCalled();

    setContext("browserShell", false);
    setContext("newSessionPromptFocused", false);
    paste();
    expect(commandState.run).not.toHaveBeenCalled();
    dispose();
    vi.unstubAllGlobals();
  });

  it("dispatches a client-owned binding through its local catalog", () => {
    commandState.entries = [
      {
        catalogBackendId: "local",
        binding: { key: "ctrl+=", command: "weavie.font.increase" },
      },
    ];
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
      key: "=",
      isComposing: false,
      ctrlKey: true,
      metaKey: false,
      shiftKey: false,
      altKey: false,
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
    } as unknown as KeyboardEvent);

    expect(commandState.run).toHaveBeenCalledOnce();
    expect(commandState.run).toHaveBeenCalledWith("local", "weavie.font.increase", undefined);
    dispose();
    vi.unstubAllGlobals();
  });
});
