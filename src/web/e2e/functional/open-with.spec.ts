import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { awaitEditorReady, createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { headlessProgram } from "../harness/test-programs";

// "Open With Weavie", full stack: a second launch carrying a path hands it to the running host over the
// instance pipe and exits without building a Core graph of its own, and the running host opens the file.
// This is the journey a file-manager double-click makes; only the OS association above it is manual.

// Launches the program again with `path`, the way a file manager does, and returns its exit code.
function handOver(path: string, weavieRoot: string): Promise<number | null> {
  return new Promise((resolve, reject) => {
    const child = spawn(headlessProgram.command, [...headlessProgram.args, path], {
      env: { ...process.env, WEAVIE_ROOT: weavieRoot },
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
}

test("a second launch hands its path to the running Weavie instead of booting another", async ({
  page,
  weavie,
}) => {
  await awaitEditorReady(page);

  const outsideDir = await mkdtemp(join(tmpdir(), "weavie-openwith-"));
  try {
    const handed = join(outsideDir, "handed-over.md");
    await writeFile(handed, "# Handed over\n");

    // Exit 0 without serving anything is the proof it handed over rather than starting a second app.
    expect(await handOver(handed, join(weavie.home, ".weavie"))).toBe(0);
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /handed-over\.md$/);
  } finally {
    await rm(outsideDir, { recursive: true, force: true });
  }
});

// The file lands in the session the user is looking at, not the workspace checkout. The host forwards the path
// rather than choosing a session, because the selected one can belong to a different backend entirely.
test("a handed-over path opens in the selected session, not the workspace one", async ({
  page,
  weavie,
}) => {
  await awaitEditorReady(page);
  await createSession(page, { branch: "open-with-target", provider: "fake-acp" });

  const outsideDir = await mkdtemp(join(tmpdir(), "weavie-openwith-sel-"));
  try {
    const handed = join(outsideDir, "into-selected.md");
    await writeFile(handed, "# Into the selected session\n");

    expect(await handOver(handed, join(weavie.home, ".weavie"))).toBe(0);

    // .editor renders the selected session, so the file appearing there is the assertion that it went to the
    // session in front rather than the workspace checkout behind it.
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /into-selected\.md$/);
    await expect(page.locator('.session-chip.active[title^="open-with-target —"]')).toBeVisible();
  } finally {
    await rm(outsideDir, { recursive: true, force: true });
  }
});
