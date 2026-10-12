import { type ChildProcess, spawn } from "node:child_process";
import { rm } from "node:fs/promises";
import { expect } from "@playwright/test";
import { openWorkspace, waitForWorkspace } from "./capture-workspace.mjs";
import { test } from "./harness/network-fixtures";
import { makeTempDir } from "./harness/temp-root";
import { headlessProgram, programExists } from "./harness/test-programs";
import { killProcessTree } from "./harness/weavie-host";

// End-to-end guard for the browser <-> WebSocket <-> Weavie.Core path: spawn the real built headless host,
// point a browser at it, and prove the bridge round-trips into the C# session. Skipped when the host hasn't
// been built, so the web-only `pnpm run e2e` still runs.

test.describe("headless host (real Weavie.Core over WebSocket)", () => {
  test.skip(
    !programExists(headlessProgram),
    "Weavie.Headless not built (run `dotnet build src/Weavie.Headless`)",
  );

  let proc: ChildProcess | undefined;
  let workspace: string | undefined;
  let workspacePage: { pageUrl: string; token: string };

  test.beforeAll(async () => {
    // A throwaway workspace so the test never mutates the repo or collides on the editor-session file.
    workspace = await makeTempDir("weavie-e2e-");
    proc = spawn(headlessProgram.command, headlessProgram.args, {
      env: {
        ...process.env,
        // Port 0: the OS assigns a free port at bind; the ready line reports it once it is accepting.
        WEAVIE_SERVE_PORT: "0",
        WEAVIE_SERVE_WORKSPACE: workspace,
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    workspacePage = await waitForWorkspace(proc, 30_000);
  });

  test.afterAll(async () => {
    if (proc !== undefined) await killProcessTree(proc);
    if (workspace !== undefined) await rm(workspace, { recursive: true, force: true });
  });

  test("a browser completes the host hello and renders the returned session catalog", async ({
    page,
  }) => {
    await openWorkspace(page, workspacePage);

    // The host injected the bridge URL, so the web picked the WebSocket transport. (String form so the
    // browser-only `window` global isn't referenced in this Node test module.)
    await expect.poll(() => page.evaluate("window.__WEAVIE_BRIDGE_WS__")).toBe("auto");

    // The catalog can render only after connection.hello crossed into C# and its response crossed back.
    await expect(page.locator(".session-chip").first()).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".footer-network-problem")).toHaveCount(0);
  });
});
