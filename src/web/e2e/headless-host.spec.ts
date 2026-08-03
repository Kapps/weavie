import { type ChildProcess, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { openWorkspace, waitForWorkspace } from "./capture-workspace.mjs";

// End-to-end guard for the browser <-> WebSocket <-> Weavie.Core path: spawn the real built headless host,
// point a browser at it, and prove the bridge round-trips into the C# session. Skipped when the host hasn't
// been built, so the web-only `pnpm run e2e` still runs.

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
const hostDll = join(
  repoRoot,
  "src",
  "Weavie.Headless",
  "bin",
  "Debug",
  "net10.0",
  "Weavie.Headless.dll",
);

test.describe("headless host (real Weavie.Core over WebSocket)", () => {
  test.skip(
    !existsSync(hostDll),
    "Weavie.Headless not built (run `dotnet build src/Weavie.Headless`)",
  );

  let proc: ChildProcess;
  let workspacePage: { pageUrl: string; token: string };

  test.beforeAll(async () => {
    // A throwaway workspace so the test never mutates the repo or collides on the editor-session file.
    const workspace = await mkdtemp(join(tmpdir(), "weavie-e2e-"));
    proc = spawn("dotnet", [hostDll], {
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

  test.afterAll(() => {
    proc?.kill("SIGINT");
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
