import { beforeEach, describe, expect, it, vi } from "vitest";
import { setContext } from "./context";
import type { CommandInfo, ResolvedKeybinding } from "./types";

const commandState = vi.hoisted(() => ({
  activeBackendId: "local",
  localBindings: [] as ResolvedKeybinding[],
  localCommands: [] as CommandInfo[],
  remoteBindings: [] as ResolvedKeybinding[],
  run: vi.fn(() => true),
}));

// keybindings.ts pulls in the registry (and through it the window-coupled bridge) only for the resolver;
// formatKey itself needs none of it. Stub the registry so the module loads in the pure node env.
vi.mock("./registry", () => ({
  getActiveCatalogBackendId: () => commandState.activeBackendId,
  getCommandsInCatalog: (backendId: string) =>
    backendId === "local" ? commandState.localCommands : [],
  getKeybindingsInCatalog: (backendId: string) =>
    backendId === "local" ? commandState.localBindings : commandState.remoteBindings,
  LOCAL_COMMAND_CATALOG_ID: "local",
  onCommandsChanged: () => () => {},
  runForKeybindingFromCatalog: commandState.run,
}));

const { formatKey, installKeybindings } = await import("./keybindings");

beforeEach(() => {
  commandState.activeBackendId = "local";
  commandState.localBindings = [];
  commandState.localCommands = [];
  commandState.remoteBindings = [];
  commandState.run.mockClear();
  setContext("nativeShell", true);
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
    commandState.localBindings = [
      { key: "ctrl+shift+tab", command: "weavie.session.prev", args: undefined },
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
    commandState.localBindings = [
      { key: "enter", command: "weavie.agent.submit", args: undefined },
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

  it("keeps local native-shell bindings active while a remote backend is selected", () => {
    commandState.activeBackendId = "remote:r";
    commandState.localCommands = [
      {
        id: "weavie.window.exit",
        title: "Exit Weavie",
        runsIn: "web",
        description: "",
        aliases: [],
        showInPalette: true,
        when: "nativeShell",
        keys: ["ctrl+q"],
      },
    ];
    commandState.localBindings = [
      { key: "ctrl+q", command: "weavie.window.exit", when: "nativeShell" },
    ];
    commandState.remoteBindings = [{ key: "ctrl+q", command: "remote.other", args: undefined }];
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
      key: "q",
      isComposing: false,
      ctrlKey: true,
      metaKey: false,
      shiftKey: false,
      altKey: false,
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
    } as unknown as KeyboardEvent);

    expect(commandState.run).toHaveBeenCalledWith("local", "weavie.window.exit", undefined);
    dispose();
    vi.unstubAllGlobals();
  });
});
