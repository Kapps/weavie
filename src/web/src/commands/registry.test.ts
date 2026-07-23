import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CommandInfo, CommandResult } from "./types";

const env = vi.hoisted(() => ({
  hostHandlers: [] as Array<(m: Record<string, unknown>) => void>,
  posted: [] as Array<Record<string, unknown>>,
  invokeCalls: [] as Array<{ backendId: string; id: string; args: unknown }>,
  notified: [] as Array<{ level: string; message: unknown }>,
  coreResult: { ok: true, data: "core-ran" } as CommandResult,
  active: "local",
}));

vi.mock("../bridge", () => ({
  activeBackendId: () => env.active,
  hostInjected: <T>(_name: string, value: T | undefined, fallback: T): T => value ?? fallback,
  invokeCommandOnBackend: (
    backendId: string,
    id: string,
    args: unknown,
  ): Promise<CommandResult> => {
    env.invokeCalls.push({ backendId, id, args });
    return Promise.resolve(env.coreResult);
  },
  log: () => {},
  onHostMessage: (h: (m: Record<string, unknown>) => void) => {
    env.hostHandlers.push(h);
    return () => {};
  },
  postToHost: (m: Record<string, unknown>) => {
    env.posted.push(m);
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

const reg = await import("./registry");

function cmd(id: string, runsIn: "web" | "core"): CommandInfo {
  return {
    id,
    title: id,
    runsIn,
    description: "",
    aliases: [],
    showInPalette: true,
    keys: [],
  };
}
const setCatalog = (commands: CommandInfo[]): void => {
  for (const h of env.hostHandlers) {
    h({ type: "commands", commands, keybindings: [] });
  }
};

beforeEach(() => {
  env.posted.length = 0;
  env.invokeCalls.length = 0;
  env.notified.length = 0;
  env.coreResult = { ok: true, data: "core-ran" };
  env.active = "local";
  setCatalog([]);
});

describe("dispatchCommand — web commands", () => {
  it("runs the registered handler and resolves ok", async () => {
    setCatalog([cmd("web.a", "web")]);
    let ran = false;
    reg.registerCommand("web.a", () => {
      ran = true;
    });
    expect(await reg.dispatchCommand("web.a")).toEqual({ ok: true });
    expect(ran).toBe(true);
  });

  it("maps an explicit false return onto ok:false (declined)", async () => {
    setCatalog([cmd("web.b", "web")]);
    reg.registerCommand("web.b", () => false);
    expect(await reg.dispatchCommand("web.b")).toEqual({ ok: false });
  });

  it("catches a throwing handler and reports the error", async () => {
    setCatalog([cmd("web.c", "web")]);
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
    setCatalog([cmd("web.d", "web")]);
    const res = await reg.dispatchCommand("web.d");
    expect(res.ok).toBe(false);
    expect(res.error).toMatch(/web handler/);
  });
});

describe("dispatchCommand — core commands", () => {
  it("routes a core command to the active backend and returns its result", async () => {
    setCatalog([cmd("core.x", "core")]);
    const res = await reg.dispatchCommand("core.x", { foo: 1 });
    expect(res).toMatchObject({ ok: true, data: "core-ran" });
    expect(env.invokeCalls).toEqual([{ backendId: "local", id: "core.x", args: { foo: 1 } }]);
  });

  it("honours an explicit backendId arg over the active backend", async () => {
    setCatalog([cmd("core.y", "core")]);
    await reg.dispatchCommand("core.y", { backendId: "remote:r" });
    expect(env.invokeCalls[0]?.backendId).toBe("remote:r");
  });
});

describe("runCommandWithFeedback", () => {
  // A Core command's silent success arrives over JSON as message:null (not undefined); it must not toast —
  // otherwise a normal font zoom spams empty toasts (only the ✕ close button shows).
  it("does not toast a silent core success (message is null over the wire)", async () => {
    setCatalog([cmd("core.silent", "core")]);
    env.coreResult = { ok: true, message: null, error: null } as unknown as CommandResult;
    await reg.runCommandWithFeedback("core.silent");
    expect(env.notified).toEqual([]);
  });

  it("toasts an informational core message", async () => {
    setCatalog([cmd("core.info", "core")]);
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
    setCatalog([cmd("core.fail", "core")]);
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
    setCatalog([cmd("web.k", "web")]);
    reg.registerCommand("web.k", () => undefined);
    expect(reg.runForKeybinding("web.k", undefined)).toBe(true);
  });

  it("lets the key fall through when the handler declines with false", () => {
    setCatalog([cmd("web.k2", "web")]);
    reg.registerCommand("web.k2", () => false);
    expect(reg.runForKeybinding("web.k2", undefined)).toBe(false);
  });

  it("declines an unknown command", () => {
    expect(reg.runForKeybinding("nope", undefined)).toBe(false);
  });

  it("fires a core command and consumes the key without awaiting", () => {
    setCatalog([cmd("core.k", "core")]);
    expect(reg.runForKeybinding("core.k", undefined)).toBe(true);
    expect(env.invokeCalls[0]?.id).toBe("core.k");
  });

  it("surfaces a thrown web handler as a toast instead of a silent console log", () => {
    setCatalog([cmd("web.kthrow", "web")]);
    reg.registerCommand("web.kthrow", () => {
      throw new Error("kboom");
    });
    expect(reg.runForKeybinding("web.kthrow", undefined)).toBe(true);
    expect(env.notified).toEqual([{ level: "warn", message: "Error: kboom" }]);
  });

  it("surfaces a rejecting async web handler as a toast", async () => {
    setCatalog([cmd("web.kreject", "web")]);
    reg.registerCommand("web.kreject", () => Promise.reject(new Error("kreject")));
    expect(reg.runForKeybinding("web.kreject", undefined)).toBe(true);
    await Promise.resolve();
    await Promise.resolve();
    expect(env.notified).toEqual([{ level: "warn", message: "Error: kreject" }]);
  });
});

describe("host run-command (MCP) acknowledgement", () => {
  it("runs the web handler and acks success", async () => {
    setCatalog([cmd("web.r", "web")]);
    reg.registerCommand("web.r", () => {});
    for (const h of env.hostHandlers) {
      h({ type: "run-command", id: "web.r", args: undefined, token: "t1" });
    }
    await Promise.resolve();
    expect(env.posted).toContainEqual({ type: "command-ack", token: "t1", ok: true });
  });

  it("acks failure when no handler is registered", async () => {
    setCatalog([cmd("web.none", "web")]);
    for (const h of env.hostHandlers) {
      h({ type: "run-command", id: "web.none", args: undefined, token: "t2" });
    }
    await Promise.resolve();
    const ack = env.posted.find((m) => m.token === "t2");
    expect(ack).toMatchObject({ type: "command-ack", ok: false });
  });

  it("acks an async command failure so MCP callers do not receive a false success", async () => {
    setCatalog([cmd("web.add-word", "web")]);
    reg.registerCommand("web.add-word", () => Promise.reject(new Error("dictionary is read-only")));
    for (const h of env.hostHandlers) {
      h({ type: "run-command", id: "web.add-word", args: { word: "teh" }, token: "t3" });
    }
    await Promise.resolve();
    await Promise.resolve();
    expect(env.posted).toContainEqual({
      type: "command-ack",
      token: "t3",
      ok: false,
      error: "Error: dictionary is read-only",
    });
  });

  it("acks an asynchronously declined command as a failure", async () => {
    setCatalog([cmd("web.decline", "web")]);
    reg.registerCommand("web.decline", async () => false);
    for (const h of env.hostHandlers) {
      h({ type: "run-command", id: "web.decline", args: undefined, token: "t4" });
    }
    await Promise.resolve();
    expect(env.posted).toContainEqual({
      type: "command-ack",
      token: "t4",
      ok: false,
      error: "Command 'web.decline' declined.",
    });
  });

  it("ignores a replayed run-command for a token already in flight", async () => {
    setCatalog([cmd("web.dedup", "web")]);
    let calls = 0;
    // A handler that stays pending so the replay arrives while the first run is in flight.
    reg.registerCommand("web.dedup", () => {
      calls += 1;
      return new Promise<void>((resolve) => setTimeout(resolve, 0));
    });
    for (const h of env.hostHandlers) {
      h({ type: "run-command", id: "web.dedup", args: undefined, token: "dup" });
      h({ type: "run-command", id: "web.dedup", args: undefined, token: "dup" });
    }
    await new Promise((r) => setTimeout(r, 5));
    expect(calls).toBe(1);
    expect(env.posted.filter((m) => m.token === "dup")).toHaveLength(1);
  });
});
