import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const env = vi.hoisted(() => ({
  install: undefined as ((session: ClientSession) => undefined | (() => void)) | undefined,
}));

vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    env.install = installer;
    return () => {};
  },
}));

const store = await import("./shell-terminal-store");

function connect(): {
  session: ClientSession;
  catalog: (ids: string[]) => void;
  dispose: () => void;
} {
  let onCatalog = (_message: { terminals: Array<{ id: string }> }): void => {};
  const session = {
    feature: (name: string) => {
      expect(name).toBe("terminal.shell");
      return {
        on: (event: string, handler: (message: { terminals: Array<{ id: string }> }) => void) => {
          expect(event).toBe("catalog");
          onCatalog = handler;
          return () => {};
        },
      };
    },
  } as unknown as ClientSession;
  const cleanup = env.install?.(session);
  return {
    session,
    catalog: (ids) => onCatalog({ terminals: ids.map((id) => ({ id })) }),
    dispose: cleanup ?? (() => {}),
  };
}

let dispose = (): void => {};
beforeEach(() => {
  dispose();
  dispose = (): void => {};
});

describe("shell terminal store", () => {
  it("distinguishes an empty received catalog from a catalog that has not arrived", () => {
    const connection = connect();
    dispose = connection.dispose;
    expect(store.shellTerminalCatalogReceived(connection.session)).toBe(false);

    connection.catalog([]);

    expect(store.shellTerminalCatalogReceived(connection.session)).toBe(true);
    expect(store.shellTerminals(connection.session)).toEqual([]);
  });

  it("selects the first restored terminal and preserves explicit selection when tabs are added", () => {
    const connection = connect();
    dispose = connection.dispose;
    connection.catalog(["a", "b"]);
    expect(store.activeShellTerminalId(connection.session)).toBe("a");

    expect(store.selectShellTerminal(connection.session, "b")).toBe(true);
    connection.catalog(["a", "b", "c"]);

    expect(store.activeShellTerminalId(connection.session)).toBe("b");
    expect(store.shellTerminals(connection.session).map(({ id }) => id)).toEqual(["a", "b", "c"]);
  });

  it("selects the adjacent tab when the active terminal closes", () => {
    const connection = connect();
    dispose = connection.dispose;
    connection.catalog(["a", "b", "c"]);
    store.selectShellTerminal(connection.session, "b");

    connection.catalog(["a", "c"]);

    expect(store.activeShellTerminalId(connection.session)).toBe("c");
  });

  it("wraps in both directions and declines with fewer than two tabs", () => {
    const connection = connect();
    dispose = connection.dispose;
    connection.catalog(["a", "b"]);

    expect(store.stepShellTerminal(connection.session, -1)).toBe(true);
    expect(store.activeShellTerminalId(connection.session)).toBe("b");
    expect(store.stepShellTerminal(connection.session, 1)).toBe(true);
    expect(store.activeShellTerminalId(connection.session)).toBe("a");

    connection.catalog(["a"]);
    expect(store.stepShellTerminal(connection.session, 1)).toBe(false);
  });
});
