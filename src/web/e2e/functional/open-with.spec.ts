import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { awaitEditorReady } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { headlessProgram } from "../harness/test-programs";

// "Open With Weavie", full stack: a second launch carrying a path hands it to the running host over the
// instance pipe and exits without building a Core graph of its own, and the running host opens the file.
// This is the journey a file-manager double-click makes; only the OS association above it is manual.

test("a second launch hands its path to the running Weavie instead of booting another", async ({
  page,
  weavie,
}) => {
  await awaitEditorReady(page);

  const outsideDir = await mkdtemp(join(tmpdir(), "weavie-openwith-"));
  try {
    const handed = join(outsideDir, "handed-over.md");
    await writeFile(handed, "# Handed over\n");

    const exitCode = await new Promise<number | null>((resolve, reject) => {
      const child = spawn(headlessProgram.command, [...headlessProgram.args, handed], {
        env: { ...process.env, WEAVIE_ROOT: join(weavie.home, ".weavie") },
        stdio: "ignore",
      });
      // On regression it boots a whole host instead of handing over; kill it rather than orphan it.
      const kill = setTimeout(() => child.kill("SIGKILL"), 20_000);
      child.on("error", (error) => {
        clearTimeout(kill);
        reject(error);
      });
      child.on("exit", (code) => {
        clearTimeout(kill);
        resolve(code);
      });
    });

    // Exit 0 without serving anything is the proof it handed over rather than starting a second app.
    expect(exitCode).toBe(0);
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /handed-over\.md$/);
  } finally {
    await rm(outsideDir, { recursive: true, force: true });
  }
});
