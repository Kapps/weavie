import { spawn } from "node:child_process";
import { mkdir, open, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { test as base, type Request } from "@playwright/test";

export const test = base.extend<{ networkDiagnostics: undefined }>({
  launchOptions: async ({ launchOptions }, use, workerInfo) => {
    const directory = join(
      workerInfo.project.outputDir,
      `network-worker-${workerInfo.workerIndex}`,
    );
    await mkdir(directory, { recursive: true });
    await use({
      ...launchOptions,
      args: [...(launchOptions.args ?? []), `--log-net-log=${join(directory, "netlog.json")}`],
    });
  },
  networkDiagnostics: [
    async ({ context }, use, testInfo) => {
      const failures: string[] = [];
      let snapshot: Promise<void> | undefined;
      // Flake (Windows only): 2026-09-09 16:18 UTC, run
      // https://github.com/Kapps/weavie/actions/runs/34374758357/job/102546252324 —
      // `palette-focus-gated.spec.ts` hit ERR_NO_BUFFER_SPACE on a loopback asset GET during page load.
      // The captured windows-network.txt rules out ephemeral-port exhaustion (98 total TCP rows against a
      // 16384-port dynamic range); the host log shows ~20 git.exe subprocesses spawned by the host within
      // the same instant, at workspace open. Not confirmed as the trigger — this is the first sample since
      // the capture above was added — but it's the only correlated resource spike in the log and worth
      // checking first if this recurs.
      const onRequestFailed = (request: Request): void => {
        const error = request.failure()?.errorText ?? "unknown";
        failures.push(`${new Date().toISOString()} ${request.method()} ${request.url()} ${error}`);
        if (error !== "net::ERR_NO_BUFFER_SPACE" || snapshot !== undefined) {
          return;
        }
        snapshot = (async () => {
          if (process.platform !== "win32") {
            return;
          }
          const output = await open(testInfo.outputPath("windows-network.txt"), "w");
          try {
            await new Promise<void>((resolve, reject) => {
              const collector = spawn(
                "powershell.exe",
                [
                  "-NoProfile",
                  "-NonInteractive",
                  "-Command",
                  "$ErrorActionPreference = 'Stop'; " +
                    "netsh int ipv4 show dynamicport tcp; " +
                    "netsh int ipv4 show excludedportrange protocol=tcp; " +
                    "Get-NetTCPConnection | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess | ConvertTo-Csv -NoTypeInformation; " +
                    "Get-Process | Select-Object Id,ProcessName,HandleCount,WorkingSet64,PrivateMemorySize64 | ConvertTo-Csv -NoTypeInformation",
                ],
                { stdio: ["ignore", output.fd, output.fd] },
              );
              collector.once("error", reject);
              collector.once("close", (code) => {
                if (code === 0) resolve();
                else
                  reject(
                    new Error(
                      `Windows network capture exited with ${code}; see windows-network.txt`,
                    ),
                  );
              });
            });
          } finally {
            await output.close();
          }
        })();
        // Observe immediately; teardown awaits and reports collection failures.
        snapshot.catch(() => {});
      };
      context.on("requestfailed", onRequestFailed);
      try {
        await use(undefined);
      } finally {
        context.off("requestfailed", onRequestFailed);
        if (failures.length > 0) {
          await writeFile(testInfo.outputPath("network-failures.txt"), failures.join("\n"));
        }
        await snapshot;
      }
      if (snapshot !== undefined) {
        // Recurred on Windows CI 2026-09-09 16:07 UTC (run 34374758357, shard 4/6, mid palette-focus-gated.spec.ts)
        // and 2026-09-09 06:07 UTC (run 34317635773). Investigated: no single test triggers it — it hits whatever
        // test is running when the runner's TCP port pool is exhausted. windows-network.txt above captures the
        // diagnostic; no root cause identified yet from available data, so no fix applied here.
        // Recurred again 2026-09-15 15:07 UTC (run 34986302881, shard 3/6, editing.spec.ts) — see open PR #900
        // (ruled out ephemeral-port exhaustion and the chrome-headless-shell process count as leads; confirmed
        // the ~20 git.exe-spawn-burst-at-boot correlation across every sample so far) and open PR #901 (found and
        // fixed a real duplicate: HostCore.PushRefLinkBase re-ran `git config --get remote.origin.url` from
        // scratch on every SyncSession with no caching; now memoized). Recurred again 2026-09-16 02:15 UTC
        // (run 35046360832, shard 6/6, mobile.spec.ts:579 "Claude Code accepts back swipes beside the screen
        // edge, never on it") — same signature, no new diagnostic beyond what #900/#901 already captured;
        // not re-investigated here since #901's fix (pending merge) already targets this exact spawn burst.
        throw new Error(`Browser socket allocation failed:\n${failures.join("\n")}`);
      }
    },
    { auto: true },
  ],
});
