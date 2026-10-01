import { type ChildProcess, spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { killProcessTree } from "./harness/weavie-host";

test("POSIX shutdown waits for inherited pipes after the root exits", async () => {
  test.skip(process.platform === "win32", "POSIX graceful shutdown contract");
  const childScript = "process.send('ready'); setInterval(() => {}, 1000)";
  const root = spawn(
    process.execPath,
    [
      "-e",
      `const {spawn} = require('child_process');
       const child = spawn(process.execPath, ['-e', ${JSON.stringify(childScript)}],
         {stdio: ['ignore', 'inherit', 'inherit', 'ipc']});
       process.on('SIGINT', () => process.exit(0));
       child.once('message', () => process.send(child.pid));`,
    ],
    { stdio: ["ignore", "pipe", "pipe", "ipc"] },
  );
  root.stdout?.resume();
  root.stderr?.resume();
  const rootClosed = once(root, "close");
  const [childPid] = await once(root, "message");
  const rootExited = once(root, "exit");
  let stopped = false;
  const stopping = killProcessTree(root).then(() => {
    stopped = true;
  });
  try {
    await rootExited;
    await new Promise(setImmediate);
    expect(stopped).toBe(false);
    expect(root.stdout?.closed).toBe(false);
  } finally {
    process.kill(childPid, "SIGTERM");
    await rootClosed;
    await stopping;
  }
  expect(stopped).toBe(true);
  expect(root.stdout?.closed).toBe(true);
  await killProcessTree(root);
});

test("Windows process-tree shutdown rejects an exited root with a surviving child", async () => {
  test.skip(process.platform !== "win32", "Windows process ownership regression");

  const dir = await mkdtemp(join(tmpdir(), "weavie-process-tree-"));
  const pidFile = join(dir, "child.pid");
  const root = spawn(
    process.execPath,
    [
      "-e",
      `const{spawn}=require('child_process');const{writeFileSync}=require('fs');const c=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{detached:true,stdio:'ignore'});c.unref();writeFileSync(${JSON.stringify(pidFile)},String(c.pid))`,
    ],
    { stdio: "ignore" },
  );
  await closed(root);
  const childPid = Number(await readFile(pidFile, "utf8"));

  try {
    expect(childPid).toBeGreaterThan(0);
    await expect(killProcessTree(root)).rejects.toThrow("exited before Windows tree shutdown");
  } finally {
    if (childPid > 0) {
      const cleanup = spawn("taskkill", ["/pid", String(childPid), "/T", "/F"], {
        stdio: "ignore",
      });
      await closed(cleanup);
      expect(cleanup.exitCode).toBe(0);
    }
    await rm(dir, { recursive: true });
  }
});

function closed(proc: ChildProcess): Promise<void> {
  if (proc.exitCode !== null || proc.signalCode !== null) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    proc.once("close", () => resolve());
    proc.once("error", reject);
  });
}
