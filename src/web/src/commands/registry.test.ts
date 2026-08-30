import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../bridge";
import { CommandIds, type CommandInfo, type CommandResult, type ResolvedKeybinding } from "./types";

const env = vi.hoisted(() => ({
  invokeCalls: [] as Array<{ backendId: string; id: string; args: unknown }>,
  notified: [] as Array<{ level: string; message: unknown }>,
  coreResult: { ok: true, data: "core-ran" } as CommandResult,
  catalogs: new Map<
    string,
    (catalog: { commands: CommandInfo[]; keybindings: unknown[] }) => void
  >(),
  installHost: undefined as ((backendId: string) => void) | undefined,
  installView: undefined as ((session: ClientSession) => void) | undefined,
  viewRuns: new Map<string, (request: { id: string; args: unknown }) => Promise<CommandResult>>(),
  run: undefined as
    | ((request: { id: string; args: unknown }) => Promise<CommandResult>)
    | undefined,
  clientRun: undefined as
    | ((request: { id: string; args: unknown }) => Promise<CommandResult>)
    | undefined,
  selected: null as ClientSession | null,
  selectedAddresses: [] as Array<{
    backendId: string;
    address: { slot: string; incarnation: string };
  }>,
  selectionCandidates: [] as ClientSession[][],
  acceptSelection: true,
  activations: [] as Array<{ session: ClientSession; created: boolean }>,
  terminalActivations: [] as Array<{ session: ClientSession; terminalId: string }>,
}));

vi.mock("../bridge", () => ({
  beginClientSelectionCandidate: () => {
    const commits: ClientSession[] = [];
    env.selectionCandidates.push(commits);
    return (session: ClientSession) => {
      if (!env.acceptSelection) {
        return false;
      }
      commits.push(session);
      return true;
    };
  },
  hostInjected: <T>(_name: string, value: T | undefined, fallback: T): T => value ?? fallback,
  LOCAL_BACKEND_ID: "local",
  invokeCommandOnBackend: (
    backendId: string,
    id: string,
    args: unknown,
  ): Promise<CommandResult> => {
    env.invokeCalls.push({ backendId, id, args });
    return Promise.resolve(env.coreResult);
  },
  invokeClientCommandOnHost: (id: string, args: unknown): Promise<CommandResult> => {
    env.invokeCalls.push({ backendId: "local", id, args });
    return Promise.resolve(env.coreResult);
  },
  invokeSessionCommandOnBackend: (
    backendId: string,
    id: string,
    args: unknown,
  ): Promise<CommandResult> => {
    env.invokeCalls.push({ backendId, id, args });
    return Promise.resolve(env.coreResult);
  },
  log: () => {},
  onSelectedSession: (listener: (session: ClientSession | null) => void) => {
    listener(env.selected);
    return () => {};
  },
  registerHostFeature: (installer: (connection: HostConnection) => undefined | (() => void)) => {
    env.installHost = (backendId: string) =>
      installer({
        id: backendId,
        onHello: () => () => {},
        host: {
          feature: () => ({
            on: (
              _name: string,
              handler: (catalog: { commands: CommandInfo[]; keybindings: unknown[] }) => void,
            ) => {
              env.catalogs.set(backendId, handler);
              return () => {};
            },
          }),
        },
      } as unknown as HostConnection);
    env.installHost("local");
    return () => {};
  },
  registerViewFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    env.installView = (session: ClientSession) => {
      installer(session);
    };
    if (env.selected !== null) {
      env.installView(env.selected);
    }
    return () => {};
  },
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    if (env.selected !== null) {
      installer(env.selected);
    }
    return () => {};
  },
  selectedSession: () => env.selected,
  waitForClientSession: (
    backendId: string,
    address: { slot: string; incarnation: string },
  ): Promise<ClientSession> => {
    env.selectedAddresses.push({ backendId, address });
    return Promise.resolve(env.selected as ClientSession);
  },
}));
// trackSessionCommand only wraps session-lifecycle ops; pass straight through for the tests.
vi.mock("../chrome/session-store", () => ({
  trackSessionCommand: <T>(_b: string, _i: string, run: () => Promise<T>) => run(),
}));
vi.mock("../notify/notify", () => ({
  notify: (level: string, message: unknown) => {
    env.notified.push({ level, message });
  },
}));

// registry reads window.__WEAVIE_* at module load.
vi.stubGlobal("window", {});

function fakeSession(slot: string, incarnation: string): ClientSession {
  const session = {
    connection: { id: "local" },
    address: { slot, incarnation },
    feature: () => ({
      handleConcurrent: (
        name: string,
        handler: (request: { id: string; args: unknown }) => Promise<CommandResult>,
      ) => {
        if (name === "runClient") {
          env.clientRun = handler;
        } else {
          if (slot === "selected-slot") {
            env.run = handler;
          }
          env.viewRuns.set(`${slot}/${incarnation}`, handler);
        }
        return () => {};
      },
    }),
  } as unknown as ClientSession;
  return session;
}

env.selected = fakeSession("selected-slot", "selected-incarnation");

const reg = await import("./registry");
env.installHost?.("remote:r");
reg.onSessionActivated((activation) => env.activations.push(activation));
reg.onTerminalActivated((activation) => env.terminalActivations.push(activation));

function cmd(id: string, runsIn: "web" | "core"): CommandInfo {
  return {
    id,
    title: id,
    runsIn,
    executionLane: id,
    scope: "session",
    description: "",
    aliases: [],
    showInPalette: true,
    keys: [],
  };
}
const setCatalog = (backendId: string, commands: CommandInfo[]): void => {
  setCatalogData(backendId, commands, []);
};
const setCatalogData = (
  backendId: string,
  commands: CommandInfo[],
  keybindings: ResolvedKeybinding[],
): void => {
  env.catalogs.get(backendId)?.({ commands, keybindings });
};

beforeEach(() => {
  env.invokeCalls.length = 0;
  env.notified.length = 0;
  env.selectedAddresses.length = 0;
  env.selectionCandidates.length = 0;
  env.activations.length = 0;
  env.terminalActivations.length = 0;
  env.acceptSelection = true;
  env.coreResult = { ok: true, data: "core-ran" };
  env.selected = {
    ...env.selected,
    connection: { id: "local" },
  } as unknown as ClientSession;
  setCatalog("local", []);
  setCatalog("remote:r", []);
});

describe("dispatchCommand — web commands", () => {
  it("runs the registered handler and resolves ok", async () => {
    setCatalog("local", [cmd("web.a", "web")]);
    let ran = false;
    reg.registerCommand("web.a", () => {
      ran = true;
    });
    expect(await reg.dispatchCommand("web.a")).toEqual({ ok: true });
    expect(ran).toBe(true);
  });

  it("maps an explicit false return onto ok:false (declined)", async () => {
    setCatalog("local", [cmd("web.b", "web")]);
    reg.registerCommand("web.b", () => false);
    expect(await reg.dispatchCommand("web.b")).toEqual({ ok: false });
  });

  it("catches a throwing handler and reports the error", async () => {
    setCatalog("local", [cmd("web.c", "web")]);
    reg.registerCommand("web.c", () => {
      throw new Error("boom");
    });
    const res = await reg.dispatchCommand("web.c");
    expect(res.ok).toBe(false);
    expect(res.error).toContain("boom");
  });

  it("fails an unknown command id", async () => {
    expect((await reg.dispatchCommand("does.not.exist")).ok).toBe(false);
  });

  it("fails a web command with no registered handler", async () => {
    setCatalog("local", [cmd("web.d", "web")]);
    const res = await reg.dispatchCommand("web.d");
    expect(res.ok).toBe(false);
    expect(res.error).toMatch(/web handler/);
  });

  it("can dispatch from the local catalog while a remote backend is selected", async () => {
    setCatalog("local", [cmd("web.local-menu", "web")]);
    let ran = false;
    reg.registerCommand("web.local-menu", () => {
      ran = true;
    });
    env.selected = {
      ...env.selected,
      connection: { id: "remote:r" },
    } as unknown as ClientSession;

    expect(await reg.dispatchCommandFromCatalog("local", "web.local-menu")).toEqual({ ok: true });
    expect(ran).toBe(true);
    expect(reg.findCommandInCatalog("local", "web.local-menu")?.id).toBe("web.local-menu");

    env.selected = {
      ...env.selected,
      connection: { id: "local" },
    } as unknown as ClientSession;
  });
});

describe("dispatchCommand — core commands", () => {
  it("routes a core command to the active backend and returns its result", async () => {
    setCatalog("local", [cmd("core.x", "core")]);
    const res = await reg.dispatchCommand("core.x", { foo: 1 });
    expect(res).toMatchObject({ ok: true, data: "core-ran" });
    expect(env.invokeCalls).toEqual([{ backendId: "local", id: "core.x", args: { foo: 1 } }]);
  });

  it("honours an explicit backendId arg over the active backend", async () => {
    setCatalog("local", [cmd("core.y", "core")]);
    await reg.dispatchCommand("core.y", { backendId: "remote:r" });
    expect(env.invokeCalls[0]?.backendId).toBe("remote:r");
  });

  it("routes a session lifecycle command to the exact selected session", async () => {
    setCatalog("local", [{ ...cmd(CommandIds.unloadSession, "core"), scope: "host" }]);

    await reg.dispatchCommand(CommandIds.unloadSession);

    expect(env.invokeCalls).toEqual([
      {
        backendId: "local",
        id: CommandIds.unloadSession,
        args: { id: "selected-slot" },
      },
    ]);
  });

  it("uses the local definition and host for client-owned commands", async () => {
    const localFont = {
      ...cmd("weavie.font.increase", "core"),
      title: "Local font command",
      owner: "client" as const,
    };
    setCatalogData("local", [localFont], [{ key: "ctrl+=", command: "weavie.font.increase" }]);
    setCatalogData(
      "remote:r",
      [
        { ...cmd("weavie.font.increase", "core"), title: "Remote duplicate" },
        cmd("weavie.terminal.reopen", "core"),
      ],
      [
        { key: "alt+=", command: "weavie.font.increase" },
        { key: "ctrl+t", command: "weavie.terminal.reopen" },
      ],
    );
    env.selected = {
      ...env.selected,
      connection: { id: "remote:r" },
    } as unknown as ClientSession;

    await reg.dispatchCommand("weavie.font.increase", { backendId: "remote:r" });
    await reg.dispatchCommand("weavie.terminal.reopen");

    expect(reg.findCommand("weavie.font.increase")?.title).toBe("Local font command");
    expect(reg.getKeybindings()).toEqual([
      { key: "ctrl+t", command: "weavie.terminal.reopen" },
      { key: "ctrl+=", command: "weavie.font.increase" },
    ]);
    expect(env.invokeCalls.map(({ backendId, id }) => ({ backendId, id }))).toEqual([
      { backendId: "local", id: "weavie.font.increase" },
      { backendId: "remote:r", id: "weavie.terminal.reopen" },
    ]);
  });

  it("activates the exact session requested by a successful command result", async () => {
    setCatalog("local", [cmd("core.create", "core")]);
    env.coreResult = {
      ok: true,
      data: {
        address: { slot: "branch-a", incarnation: "incarnation-a" },
        activateSession: true,
        createdSession: true,
      },
    };

    await reg.dispatchCommand("core.create", { backendId: "remote:r" });

    expect(env.selectedAddresses).toEqual([
      {
        backendId: "remote:r",
        address: { slot: "branch-a", incarnation: "incarnation-a" },
      },
    ]);
    expect(env.selectionCandidates).toEqual([[env.selected]]);
    expect(env.activations).toEqual([{ session: env.selected, created: true }]);
  });

  it("selects an existing session without reporting it as newly created", async () => {
    setCatalog("local", [cmd("core.open", "core")]);
    env.coreResult = {
      ok: true,
      data: {
        address: { slot: "branch-a", incarnation: "incarnation-a" },
        activateSession: true,
      },
    };

    await reg.dispatchCommand("core.open");

    expect(env.selectionCandidates).toEqual([[env.selected]]);
    expect(env.activations).toEqual([{ session: env.selected, created: false }]);
  });

  it("does not report a stale created-session result that loses the selection race", async () => {
    setCatalog("local", [cmd("core.create", "core")]);
    env.acceptSelection = false;
    env.coreResult = {
      ok: true,
      data: {
        address: { slot: "branch-a", incarnation: "incarnation-a" },
        activateSession: true,
        createdSession: true,
      },
    };

    await reg.dispatchCommand("core.create");

    expect(env.selectionCandidates).toEqual([[]]);
    expect(env.activations).toEqual([]);
  });

  it("does not activate address-bearing background command results", async () => {
    setCatalog("local", [cmd("core.load", "core")]);
    env.coreResult = {
      ok: true,
      data: { address: { slot: "branch-a", incarnation: "incarnation-a" } },
    };

    await reg.dispatchCommand("core.load");

    expect(env.selectedAddresses).toEqual([]);
    expect(env.selectionCandidates).toEqual([[]]);
  });

  it("activates the exact terminal requested by a successful command result", async () => {
    setCatalog("local", [cmd("core.new-terminal", "core")]);
    env.coreResult = {
      ok: true,
      data: {
        address: { slot: "branch-a", incarnation: "incarnation-a" },
        activateTerminal: true,
        terminalId: "terminal-b",
      },
    };

    await reg.dispatchCommand("core.new-terminal", { backendId: "remote:r" });

    expect(env.selectedAddresses).toEqual([
      {
        backendId: "remote:r",
        address: { slot: "branch-a", incarnation: "incarnation-a" },
      },
    ]);
    expect(env.selectionCandidates).toEqual([[]]);
    expect(env.terminalActivations).toEqual([{ session: env.selected, terminalId: "terminal-b" }]);
  });
});

describe("runCommandWithFeedback", () => {
  // A Core command's silent success arrives over JSON as message:null (not undefined); it must not toast —
  // otherwise a normal font zoom spams empty toasts (only the ✕ close button shows).
  it("does not toast a silent core success (message is null over the wire)", async () => {
    setCatalog("local", [cmd("core.silent", "core")]);
    env.coreResult = { ok: true, message: null, error: null } as unknown as CommandResult;
    await reg.runCommandWithFeedback("core.silent");
    expect(env.notified).toEqual([]);
  });

  it("toasts an informational core message", async () => {
    setCatalog("local", [cmd("core.info", "core")]);
    env.coreResult = {
      ok: true,
      message: "Font size is already at its maximum (16px).",
    } as CommandResult;
    await reg.runCommandWithFeedback("core.info");
    expect(env.notified).toEqual([
      { level: "info", message: "Font size is already at its maximum (16px)." },
    ]);
  });

  it("toasts a core failure error", async () => {
    setCatalog("local", [cmd("core.fail", "core")]);
    env.coreResult = {
      ok: false,
      message: null,
      error: "No active session.",
    } as unknown as CommandResult;
    await reg.runCommandWithFeedback("core.fail");
    expect(env.notified).toEqual([{ level: "warn", message: "No active session." }]);
  });
});

describe("runForKeybinding", () => {
  it("consumes the key when a web handler does not decline", () => {
    setCatalog("local", [cmd("web.k", "web")]);
    reg.registerCommand("web.k", () => undefined);
    expect(reg.runForKeybinding("web.k", undefined)).toBe(true);
  });

  it("lets the key fall through when the handler declines with false", () => {
    setCatalog("local", [cmd("web.k2", "web")]);
    reg.registerCommand("web.k2", () => false);
    expect(reg.runForKeybinding("web.k2", undefined)).toBe(false);
  });

  it("declines an unknown command", () => {
    expect(reg.runForKeybinding("nope", undefined)).toBe(false);
  });

  it("fires a core command and consumes the key without awaiting", () => {
    setCatalog("local", [cmd("core.k", "core")]);
    expect(reg.runForKeybinding("core.k", undefined)).toBe(true);
    expect(env.invokeCalls[0]?.id).toBe("core.k");
  });

  it("surfaces a thrown web handler as a toast instead of a silent console log", () => {
    setCatalog("local", [cmd("web.kthrow", "web")]);
    reg.registerCommand("web.kthrow", () => {
      throw new Error("kboom");
    });
    expect(reg.runForKeybinding("web.kthrow", undefined)).toBe(true);
    expect(env.notified).toEqual([{ level: "warn", message: "Error: kboom" }]);
  });

  it("surfaces a rejecting async web handler as a toast", async () => {
    setCatalog("local", [cmd("web.kreject", "web")]);
    reg.registerCommand("web.kreject", () => Promise.reject(new Error("kreject")));
    expect(reg.runForKeybinding("web.kreject", undefined)).toBe(true);
    await Promise.resolve();
    await Promise.resolve();
    expect(env.notified).toEqual([{ level: "warn", message: "Error: kreject" }]);
  });
});

describe("session-bound web command requests", () => {
  it("runs the web handler and responds with success", async () => {
    setCatalog("local", [cmd("web.r", "web")]);
    reg.registerCommand("web.r", () => {});
    expect(await env.run?.({ id: "web.r", args: undefined })).toEqual({ ok: true });
  });

  it("responds with failure when no handler is registered", async () => {
    setCatalog("local", [cmd("web.none", "web")]);
    expect(await env.run?.({ id: "web.none", args: undefined })).toMatchObject({
      ok: false,
      error: expect.stringContaining("No web handler"),
    });
  });

  it("orders related commands without blocking another execution lane", async () => {
    const paste = { ...cmd("weavie.agent.paste", "web"), executionLane: "agent-input" };
    const submit = { ...cmd("weavie.agent.submit", "web"), executionLane: "agent-input" };
    const showLogs = cmd("weavie.view.logs", "web");
    setCatalog("local", [paste, submit, showLogs]);
    let releasePaste = (): void => {};
    const pasteBlocked = new Promise<void>((resolve) => {
      releasePaste = resolve;
    });
    let pasteEntered = false;
    let submitRan = false;
    reg.registerCommand(paste.id, async () => {
      pasteEntered = true;
      await pasteBlocked;
    });
    reg.registerCommand(submit.id, () => {
      submitRan = true;
    });
    reg.registerCommand(showLogs.id, () => {});

    const pasteResponse = env.run?.({ id: paste.id, args: undefined });
    await vi.waitFor(() => expect(pasteEntered).toBe(true));
    const submitResponse = env.run?.({ id: submit.id, args: undefined });

    expect(await env.run?.({ id: showLogs.id, args: undefined })).toEqual({ ok: true });
    expect(submitRan).toBe(false);

    releasePaste();
    await pasteResponse;
    expect(await submitResponse).toEqual({ ok: true });
    expect(submitRan).toBe(true);
  });

  it("does not share an execution lane between exact session owners", async () => {
    const command = { ...cmd("web.owner-lane", "web"), executionLane: "shared-lane" };
    setCatalog("local", [command]);
    let releaseSelected = (): void => {};
    const selectedBlocked = new Promise<void>((resolve) => {
      releaseSelected = resolve;
    });
    reg.registerCommand(command.id, async (_args, { session }) => {
      if (session?.address.slot === "selected-slot") {
        await selectedBlocked;
      }
    });
    const other = fakeSession("other-slot", "other-incarnation");
    env.installView?.(other);
    const selectedRun = env.viewRuns.get("selected-slot/selected-incarnation");
    const otherRun = env.viewRuns.get("other-slot/other-incarnation");

    const selectedResponse = selectedRun?.({ id: command.id, args: undefined });
    expect(await otherRun?.({ id: command.id, args: undefined })).toEqual({ ok: true });

    releaseSelected();
    expect(await selectedResponse).toEqual({ ok: true });
  });
});

describe("session-bound client command requests", () => {
  it("relays a client-owned Core command to the local host", async () => {
    setCatalog("local", [{ ...cmd("core.client", "core"), owner: "client" }]);

    expect(await env.clientRun?.({ id: "core.client", args: { value: 1 } })).toMatchObject({
      ok: true,
    });
    expect(env.invokeCalls).toEqual([
      { backendId: "local", id: "core.client", args: { value: 1 } },
    ]);
  });

  it("rejects a backend-owned command on the client relay", async () => {
    setCatalog("local", [cmd("core.backend", "core")]);

    expect(await env.clientRun?.({ id: "core.backend", args: undefined })).toMatchObject({
      ok: false,
      error: expect.stringContaining("not owned"),
    });
    expect(env.invokeCalls).toEqual([]);
  });
});
