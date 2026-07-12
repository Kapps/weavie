import { type ChildProcess, spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { killProcessTree } from "./harness/weavie-host";

test("Windows process-tree shutdown reaps a child whose root already exited", async () => {
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
    expect(isAlive(childPid)).toBe(true);
    // The root is gone, so a parent-pid walk from a live root can't see the child — the closure kill still
    // must reach it through the dead root's stale ParentProcessId link. This is the orphan class that held
    // temp workspaces open (EBUSY on rmdir) when teardown raced a session's process spawns.
    await killProcessTree(root);
    expect(isAlive(childPid)).toBe(false);
  } finally {
    if (childPid > 0 && isAlive(childPid)) {
      const cleanup = spawn("taskkill", ["/pid", String(childPid), "/T", "/F"], {
        stdio: "ignore",
      });
      await closed(cleanup);
    }
    await rm(dir, { recursive: true });
  }
});

function isAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

function closed(proc: ChildProcess): Promise<void> {
  if (proc.exitCode !== null || proc.signalCode !== null) {
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    proc.once("close", () => resolve());
    proc.once("error", reject);
  });
}
