import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { expect, test } from "@playwright/test";
import { removeWorkspace } from "../harness/git-workspace";
import { killProcessTree } from "../harness/weavie-host";

let executable: string;
test.beforeAll(() => {
  const project = resolve(import.meta.dirname, "../../../../tests/Weavie.NativeBridge.Tests");
  try {
    execFileSync("dotnet", ["build", project, "-c", "Release"], { encoding: "utf8" });
  } catch (error) {
    if (error instanceof Error && "stdout" in error)
      throw new Error(String(error.stdout), { cause: error });
    throw error;
  }
  const properties = JSON.parse(
    execFileSync(
      "dotnet",
      ["msbuild", project, "-p:Configuration=Release", "-getProperty:TargetDir,AssemblyName"],
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
  test(`native policy rejects ${scenario}`, async ({ browserName: _browserName }, info) => {
    const profile = await mkdtemp(join(tmpdir(), "weavie-native-policy-"));
    const result = Promise.withResolvers<string>();
    let log = "";
    const app = createServer((req, res) => {
      if (req.url === "/leak") result.resolve("FAIL: forbidden document executed");
      res.setHeader("Content-Type", "text/html");
      res.end(`<!doctype html><body><h1>Native policy: ${scenario}</h1><script>
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
    app.listen(0, "127.0.0.1");
    foreign.listen(0, "127.0.0.1");
    await Promise.all([once(app, "listening"), once(foreign, "listening")]);
    const appUrl = `http://127.0.0.1:${(app.address() as { port: number }).port}/app`;
    const foreignUrl = `http://127.0.0.1:${(foreign.address() as { port: number }).port}`;
    const proc = spawn(executable, [appUrl, foreignUrl, scenario, profile], {
      env: { ...process.env, XDG_CACHE_HOME: profile },
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdoutPending = "";
    proc.stdout.setEncoding("utf8").on("data", (chunk: string) => {
      log += chunk;
      const lines = (stdoutPending + chunk).split(/\r?\n/);
      stdoutPending = lines.pop()!;
      for (const line of lines) {
        if (line.startsWith("PASS:") || line.startsWith("FAIL:")) result.resolve(line);
      }
    });
    proc.stderr.on("data", (chunk) => {
      log += chunk;
    });
    const exited = once(proc, "exit").then(([code, signal]) => {
      throw Error(`Native probe exited unexpectedly (${code ?? signal})`);
    });
    try {
      expect(await Promise.race([result.promise, exited])).toBe(
        `PASS: ${scenario} rejected by native policy`,
      );
    } finally {
      await info.attach("native-policy.log", { body: log, contentType: "text/plain" });
      try {
        await killProcessTree(proc);
      } finally {
        app.closeAllConnections();
        app.close();
        foreign.closeAllConnections();
        foreign.close();
        await removeWorkspace(profile);
      }
    }
  });
}
