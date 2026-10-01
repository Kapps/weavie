import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { createWriteStream } from "node:fs";
import { mkdtemp } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { finished } from "node:stream/promises";
import { test as base, expect } from "@playwright/test";
import { removeWorkspace } from "../harness/git-workspace";
import { killProcessTree } from "../harness/weavie-host";

let executable: string;
const test = base.extend<{ runProbe: (scenario: string) => Promise<string> }>({
  runProbe: async ({ browserName: _browserName }, use, info) => {
    const profile = await mkdtemp(join(tmpdir(), "weavie-native-policy-"));
    const result = Promise.withResolvers<string>();
    const logPath = info.outputPath("native-policy.log");
    const log = createWriteStream(logPath);
    let proc: ReturnType<typeof spawn> | undefined;
    const app = createServer((req, res) => {
      if (req.url === "/leak") result.resolve("FAIL: forbidden document executed");
      res.setHeader("Content-Type", "text/html");
      res.end(`<!doctype html><body><h1>Native policy probe</h1><script>
        if (self === top) window.__weaviePostMessage('ready');
        else fetch('/leak');
      </script></body>`);
    });
    const foreign = createServer((req, res) => {
      const url = new URL(req.url!, "http://localhost");
      if (url.pathname === "/redirect-untrusted")
        return void res.writeHead(302, { Location: "/untrusted" }).end();
      if (url.pathname === "/redirect-app")
        return void res.writeHead(302, { Location: url.searchParams.get("target")! }).end();
      res.setHeader("Content-Type", "text/html");
      res.end("<!doctype html><h1>Untrusted destination</h1>");
    });
    try {
      app.listen(0, "127.0.0.1");
      foreign.listen(0, "127.0.0.1");
      await Promise.all([once(app, "listening"), once(foreign, "listening")]);
      const appUrl = `http://127.0.0.1:${(app.address() as { port: number }).port}/app`;
      const foreignUrl = `http://127.0.0.1:${(foreign.address() as { port: number }).port}`;
      await use((scenario) => {
        proc = spawn(executable, [appUrl, foreignUrl, scenario, profile], {
          env: { ...process.env, XDG_CACHE_HOME: profile },
          stdio: ["ignore", "pipe", "pipe"],
        });
        proc.stdout.pipe(log, { end: false });
        proc.stderr.pipe(log, { end: false });
        let stdoutPending = "";
        proc.stdout.setEncoding("utf8").on("data", (chunk: string) => {
          const lines = (stdoutPending + chunk).split(/\r?\n/);
          stdoutPending = lines.pop()!;
          for (const line of lines) {
            if (line.startsWith("PASS:") || line.startsWith("FAIL:")) result.resolve(line);
          }
        });
        const exited = once(proc, "exit").then(([code, signal]) => {
          throw Error(`Native probe exited unexpectedly (${code ?? signal})`);
        });
        return Promise.race([result.promise, exited]);
      });
    } finally {
      try {
        if (proc !== undefined) await killProcessTree(proc);
      } finally {
        app.closeAllConnections();
        app.close();
        foreign.closeAllConnections();
        foreign.close();
        try {
          if (proc !== undefined)
            await Promise.all([finished(proc.stdout!), finished(proc.stderr!)]);
        } finally {
          log.end();
          try {
            await finished(log);
            await info.attach("native-policy.log", { path: logPath, contentType: "text/plain" });
          } finally {
            await removeWorkspace(profile);
          }
        }
      }
    }
  },
});

test.beforeAll(() => {
  const project = resolve(import.meta.dirname, "../../../../tests/Weavie.NativeBridge.Tests");
  const runtime = process.platform === "darwin" ? [`-p:RuntimeIdentifier=osx-${process.arch}`] : [];
  try {
    execFileSync("dotnet", ["build", project, "-c", "Release", ...runtime], { encoding: "utf8" });
  } catch (error) {
    if (error instanceof Error && "stdout" in error)
      throw new Error(String(error.stdout), { cause: error });
    throw error;
  }
  const properties = JSON.parse(
    execFileSync(
      "dotnet",
      [
        "msbuild",
        project,
        "-p:Configuration=Release",
        ...runtime,
        "-getProperty:TargetDir,AssemblyName",
      ],
      { encoding: "utf8" },
    ),
  ).Properties;
  const name = properties.AssemblyName;
  executable =
    process.platform === "darwin"
      ? join(properties.TargetDir, `${name}.app`, "Contents", "MacOS", name)
      : join(properties.TargetDir, name + (process.platform === "win32" ? ".exe" : ""));
});

for (const scenario of [
  "foreign",
  "redirect",
  "data",
  "popup",
  "app-frame",
  "redirect-app-frame",
]) {
  test(`native policy rejects ${scenario}`, async ({ runProbe }) => {
    expect(await runProbe(scenario)).toBe(`PASS: ${scenario} rejected by native policy`);
  });
}
