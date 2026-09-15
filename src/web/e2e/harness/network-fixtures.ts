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
      // Flake (Windows only), confirmed mechanism as of 2026-09-15 15:07 UTC, run
      // https://github.com/Kapps/weavie/actions/runs/34986302881 (shards 3/6 job 104440525077 —
      // `editing.spec.ts` — and 6/6 job 104440525131 — `web-tab.spec.ts`): both hit
      // ERR_NO_BUFFER_SPACE/ERR_ABORTED on loopback asset GETs exactly when HostCore opens or resyncs a
      // session. This occurrence's windows-network.txt again rules out ephemeral-port exhaustion (92 rows
      // against a 16384-port range) and again shows a tight burst of ~20 `git.exe` children in the host log
      // at that same instant — reproduced deterministically on Linux too (not Windows-specific in cause; a
      // fresh Weavie.Headless against a trivial one-commit repo spawns the same ~20 `git` processes on
      // open), so this is a Weavie-side burst of concurrent process creation that Windows' CreateProcess
      // cost turns into a visible loopback failure. One confirmed duplicate is fixed: `PushRefLinkBase` (see
      // `HostCore.RefLinks.cs`) re-resolved the origin remote via a fresh `git config` on every session sync
      // instead of caching it, so every reconnect/reload re-added it to the burst; `HostSession` now resolves
      // it once and replays the cached value. The remaining ~18 come from otherwise-legitimate per-feature
      // git status/PR status/workspace-inventory calls that already run once each per sync — reducing that
      // further needs consolidating those call sites, not another retry or cap here.
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
        // Hits whatever test is running when HostCore's session-open/resync git burst (see the comment on
        // `networkDiagnostics` above) lands at the same instant as the page's own asset requests — not a
        // property of any one spec. windows-network.txt above still captures the diagnostic for the next
        // occurrence, to check whether the remaining burst size (see above) keeps producing this.
        throw new Error(`Browser socket allocation failed:\n${failures.join("\n")}`);
      }
    },
    { auto: true },
  ],
});
