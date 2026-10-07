import { type ChildProcess, execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { createWriteStream } from "node:fs";
import { cp, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { finished } from "node:stream/promises";
import { test as base, expect } from "@playwright/test";
import { killProcessTree, prepareFake } from "../harness/weavie-host";

type Desktop = { workspace: string; exited: Promise<never> };
export const test = base.extend<{
  desktopDocument: "app" | "bridge";
  desktop: (driver: (workspace: string) => string) => Promise<Desktop>;
}>({
  desktopDocument: ["app", { option: true }],
  desktop: async ({ browserName: _browserName, desktopDocument }, use, info) => {
    const fake = await prepareFake({
      fakeScript: null,
      workspaceSeed: null,
      inference: "disabled",
      automaticInference: false,
      setupCompleted: true,
    });
    const logPath = info.outputPath("desktop.log");
    const log = createWriteStream(logPath);
    let proc: ChildProcess | null = null;
    let launch: Promise<Desktop> | null = null;
    try {
      const start = async (driver: (workspace: string) => string) => {
        const platforms: Record<string, string> = { darwin: "Mac", win32: "Win", linux: "Linux" };
        const platform = platforms[process.platform];
        if (!platform) throw new Error(`Unsupported desktop platform: ${process.platform}`);
        const project = resolve(import.meta.dirname, `../../../Weavie.${platform}`);
        const mac = process.platform === "darwin";
        // The app under test is the published one CI ships: the .app bundle on macOS, the publish output elsewhere.
        const rid = `${{ darwin: "osx", win32: "win", linux: "linux" }[process.platform]}-${process.arch}`;
        const query = [
          "-p:Configuration=Release",
          `-p:RuntimeIdentifier=${rid}`,
          "-getProperty:TargetDir,PublishDir,AssemblyName",
        ];
        const result = execFileSync("dotnet", ["msbuild", project, ...query], { encoding: "utf8" });
        const properties = JSON.parse(result).Properties;
        const name = properties.AssemblyName;
        const app = join(fake.home, mac ? `${name}.app` : "app");
        const source = mac
          ? join(properties.TargetDir, `${name}.app`)
          : resolve(project, properties.PublishDir.replaceAll("\\", "/"));
        await cp(source, app, { recursive: true, verbatimSymlinks: true });
        const assets = mac ? join(app, "Contents", "Resources", "wwwroot") : join(app, "wwwroot");
        const script = `<script>${driver(fake.workspace).replace(/<\/script/gi, "<\\/script")}</script>`;
        for (const name of ["index.html", "welcome.html"]) {
          const file = join(assets, name);
          const html =
            desktopDocument === "app"
              ? await readFile(file, "utf8")
              : "<!doctype html><body><h1>Native bridge navigation test</h1></body>";
          await writeFile(file, html.replace("</body>", `${script}</body>`));
        }
        const signing = ["--force", "--sign", "-", "--preserve-metadata=entitlements"];
        if (mac) execFileSync("codesign", [...signing, app]);
        const recents = { version: 1, recents: [`${fake.workspace}.missing`, fake.workspace] };
        await writeFile(join(fake.home, ".weavie", "recents.json"), JSON.stringify(recents));
        const exe = mac
          ? join(app, "Contents", "MacOS", name)
          : join(app, name + (process.platform === "win32" ? ".exe" : ""));
        proc = spawn(exe, [], {
          cwd: fake.workspace,
          env: { ...process.env, ...fake.env, WEAVIE_WORKSPACE: `${fake.workspace}.missing` },
          stdio: ["ignore", "pipe", "pipe"],
        });
        for (const stream of [proc.stdout, proc.stderr]) stream?.pipe(log, { end: false });
        const exited = once(proc, "exit").then(([code, signal]): never => {
          throw new Error(`Desktop exited (${code ?? signal}); see desktop.log`);
        });
        void exited.catch(() => {});
        return { workspace: fake.workspace, exited };
      };
      await use((driver) => {
        if (launch) throw new Error("A desktop fixture owns one application process");
        launch = start(driver);
        return launch;
      });
    } finally {
      try {
        await Promise.allSettled(launch ? [launch] : []);
        if (proc) {
          await killProcessTree(proc);
          if (process.platform !== "win32")
            expect(proc.exitCode, `Desktop exited via ${proc.signalCode ?? "exit code"}`).toBe(0);
        }
      } finally {
        log.end();
        try {
          await finished(log);
          await info.attach("desktop.log", { path: logPath });
        } finally {
          await fake.cleanup();
        }
      }
    }
  },
});
