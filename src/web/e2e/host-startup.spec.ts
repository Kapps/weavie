import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import { expect, test } from "@playwright/test";
import { waitForWorkspace } from "./capture-workspace.mjs";
import { waitForOutputLines } from "./harness/process-output.mjs";

const processOutput = () =>
  Object.assign(new EventEmitter(), { stdout: new PassThrough(), stderr: new PassThrough() });

test("workspace startup waits for complete fields across every stdout split", async () => {
  for (const newline of ["\n", "\r\n"]) {
    const output =
      `[weavie-headless] token test-token${newline}` +
      `[weavie-headless] open http://127.0.0.1:43210/index.html in a browser${newline}`;
    for (let split = 1; split < output.length; split++) {
      const proc = processOutput();
      let resolved = false;
      const startup = waitForWorkspace(proc, 1000).then((workspace) => {
        resolved = true;
        return workspace;
      });
      proc.stdout.write(output.slice(0, split));
      proc.stderr.write("[weavie-headless] open http://127.0.0.1:9/\n");
      await Promise.resolve();
      expect(resolved, `premature startup at split ${split}`).toBe(false);
      proc.stdout.write(output.slice(split));
      expect(await startup).toEqual({
        pageUrl: "http://127.0.0.1:43210/index.html",
        token: "test-token",
      });
      expect(proc.stdout.listenerCount("data")).toBe(0);
      expect(proc.stderr.listenerCount("data")).toBe(0);
      expect(proc.eventNames()).toEqual([]);
    }
  }
});

test("runner startup cannot accept a partial port or stderr protocol text", async () => {
  const proc = processOutput();
  const startup = waitForOutputLines(
    proc,
    [/\[weavie-runner\] control plane:\s+(http:\/\/\S+)/],
    1000,
  );
  proc.stdout.write("[weavie-runner] control plane: http://127.0.0.1:4");
  proc.stderr.write("[weavie-runner] control plane: http://127.0.0.1:9\n");
  proc.stdout.write("3210\n");
  expect(await startup).toEqual(["http://127.0.0.1:43210"]);
});

test("early close rejects incomplete startup and retains both diagnostic streams", async () => {
  const proc = processOutput();
  const startup = waitForWorkspace(proc, 1000);
  proc.stdout.write("[weavie-headless] token test-token\n");
  proc.stdout.write("[weavie-headless] open http://127.0.0.1:4");
  proc.stderr.write("failed to initialize");
  proc.emit("close", 1);
  await expect(startup).rejects.toThrow(
    /host exited early with code 1:[\s\S]*failed to initialize/,
  );
  expect(proc.stdout.listenerCount("data")).toBe(0);
  expect(proc.stderr.listenerCount("data")).toBe(0);
  expect(proc.eventNames()).toEqual([]);
});
