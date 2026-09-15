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
      // Flake (Windows only), recurring on the `windows-latest` (2-core/7GB) hosted e2e runner: an asset GET
      // during initial page load hits ERR_NO_BUFFER_SPACE. Every sample's windows-network.txt rules out
      // ephemeral-port exhaustion (dozens to ~240 TCP rows against a 16384-port dynamic range, nowhere close);
      // every sample's weavie-host.log shows a burst of ~20 git.exe children spawned by the host in the same
      // instant, at workspace open — the one resource spike that correlates across every sample so far
      // (2026-09-09 16:18 UTC run 34374758357/102546252324; 2026-09-09 16:07 UTC run 34374758357 shard 4/6;
      // 2026-09-09 06:07 UTC run 34317635773; 2026-09-15 15:19 UTC run 34986302881/104440525077). A
      // same-instant elevated chrome-headless-shell process count is normal Chromium multi-process
      // architecture (browser+renderer+GPU), not a leak — checked and ruled out 2026-09-15. Still not
      // confirmed causal: the git burst is inherent to every workspace boot (AttachGitStatus +
      // AttachPullRequestStatus + worktree reconcile, ~10 direct spawns) but the full ~20 isn't accounted for
      // by tracing HostCore's boot path alone, and this is a shared 2-core runner where a burst of
      // Windows-Defender-scanned process creation is a plausible way to transiently starve the kernel's
      // socket-buffer pool — a runner-size change, not application code, may be the real lever.
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
        // No single test triggers it — it hits whatever test is running when the burst above lands. Most recent
        // recurrence: 2026-09-15 15:19 UTC (run 34986302881, shard 3/6, mid editing.spec.ts). Root cause still not
        // isolated from available diagnostics (see the comment above); no fix applied here.
        throw new Error(`Browser socket allocation failed:\n${failures.join("\n")}`);
      }
    },
    { auto: true },
  ],
});
