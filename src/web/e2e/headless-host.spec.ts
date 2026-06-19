import { type ChildProcess, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp } from "node:fs/promises";
import { createServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";

// Integration test against the REAL headless host (src/Weavie.Headless) rather than the MockHost: spawn the
// built host, point a real browser at it, and prove the WebSocket bridge round-trips into the C# session.
// Skipped when the host hasn't been built (so the web-only `pnpm run e2e` still runs); CI that builds the
// solution exercises it. This is the end-to-end guard for the browser <-> WebSocket <-> Weavie.Core path.

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

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = createServer();
    srv.listen(0, "127.0.0.1", () => {
      const address = srv.address();
      if (address === null || typeof address === "string") {
        reject(new Error("could not allocate a port"));
        return;
      }
      const { port } = address;
      srv.close(() => resolve(port));
    });
  });
}

// Resolve once the host logs its ready line, so the browser never races the listener.
function waitForListening(proc: ChildProcess, timeoutMs: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error("host did not report listening in time")),
      timeoutMs,
    );
    proc.stdout?.on("data", (chunk: Buffer) => {
      if (chunk.toString("utf8").includes("open  http://")) {
        clearTimeout(timer);
        resolve();
      }
    });
    proc.on("exit", (code) => {
      clearTimeout(timer);
      reject(new Error(`host exited early with code ${code}`));
    });
  });
}

test.describe("headless host (real Weavie.Core over WebSocket)", () => {
  test.skip(
    !existsSync(hostDll),
    "Weavie.Headless not built (run `dotnet build src/Weavie.Headless`)",
  );

  let proc: ChildProcess;
  let log = "";
  let port = 0;

  test.beforeAll(async () => {
    port = await freePort();
    // A throwaway workspace so the test never mutates the repo or collides on the editor-session file.
    const workspace = await mkdtemp(join(tmpdir(), "weavie-e2e-"));
    proc = spawn("dotnet", [hostDll], {
      env: { ...process.env, WEAVIE_SERVE_PORT: String(port), WEAVIE_SERVE_WORKSPACE: workspace },
      stdio: ["ignore", "pipe", "pipe"],
    });
    proc.stdout?.on("data", (chunk: Buffer) => {
      log += chunk.toString("utf8");
    });
    await waitForListening(proc, 30_000);
  });

  test.afterAll(() => {
    proc?.kill("SIGINT");
  });

  test("a browser connects over WebSocket and its `ready` reaches the C# session", async ({
    page,
  }) => {
    await page.goto(`http://127.0.0.1:${port}/`, { waitUntil: "domcontentloaded" });

    // The host injected the bridge URL, so the web picked the WebSocket transport. (String form so the
    // browser-only `window` global isn't referenced in this Node test module.)
    await expect.poll(() => page.evaluate("window.__WEAVIE_BRIDGE_WS__")).toBe("auto");

    // The page's `ready` must arrive at the real C# session — the proof the bridge round-trips end to end.
    await expect.poll(() => log, { timeout: 15_000 }).toContain('{"type":"ready"}');
  });
});
