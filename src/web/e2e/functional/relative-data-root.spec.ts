import { spawn } from "node:child_process";
import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import { expect } from "@playwright/test";
import { openWorkspace, waitForWorkspace } from "../capture-workspace.mjs";
import { test } from "../harness/network-fixtures";
import { headlessProgram } from "../harness/test-programs";
import { killProcessTree, prepareFake } from "../harness/weavie-host";

test("a relative data root keeps Claude's configuration readable from its workspace", async ({
  page,
}, info) => {
  const fake = await prepareFake({
    workspaceSeed: null,
    fakeScript: [{ op: "mcp", tool: "getSetting", args: { key: "theme.mode" } }],
    inference: "disabled",
    automaticInference: false,
    setupCompleted: true,
    markdownView: "source",
  });
  const proc = spawn(headlessProgram.command, headlessProgram.args, {
    cwd: fake.home,
    env: {
      ...process.env,
      ...fake.env,
      WEAVIE_ROOT: "./.weavie",
      WEAVIE_SERVE_PORT: "0",
      WEAVIE_SERVE_WORKSPACE: fake.workspace,
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let log = "";
  for (const stream of [proc.stdout, proc.stderr]) {
    stream.on("data", (chunk: Buffer) => {
      log += chunk.toString("utf8");
    });
  }
  try {
    await openWorkspace(page, await waitForWorkspace(proc, 40_000));
    await expect.poll(fake.fakeLog).toContain("getSetting ->");
    const hooks = join(fake.home, ".weavie", "internals", "hooks");
    const settings = (await readdir(hooks)).find((name) => name.endsWith(".settings.json"));
    expect(settings).toBeDefined();
    expect(JSON.parse(await readFile(join(hooks, settings!), "utf8"))).toHaveProperty("hooks");
  } finally {
    await info.attach("host.log", { body: log, contentType: "text/plain" });
    try {
      await killProcessTree(proc);
    } finally {
      await fake.cleanup();
    }
  }
});
