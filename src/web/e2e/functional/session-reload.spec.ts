import { type ChildProcess, execFileSync, spawn } from "node:child_process";
import { closeSync, constants, openSync } from "node:fs";
import { appendFile, readFile, rename, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { clickIntoEditor, openFile, pressDocumentStart, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test.skip(process.platform === "win32", "A stuck write needs a POSIX FIFO.");

test.use({
  workspaceSeed: {
    run: (workspace: string) =>
      appendFile(
        join(dirname(workspace), "home", ".weavie", "settings.toml"),
        "\n[messaging]\noperationDeadlineSeconds = 3\n",
      ),
  },
});

// Replaces `path` with a FIFO that serves its text to every reader, so the editor's own reads pass while a
// writer's open(2) blocks: the real write handler sticks with no test hook.
async function jamWrites(path: string): Promise<ChildProcess> {
  const seed = `${path}.seed`;
  await writeFile(seed, await readFile(path));
  execFileSync("mkfifo", [`${seed}.fifo`]);
  await rename(`${seed}.fifo`, path);
  return spawn("bash", ["-c", 'while :; do cat "$1" > "$2"; sleep 0.3; done', "--", seed, path], {
    stdio: "ignore",
    detached: true,
  });
}

const sessionChip = (page: Page, name: string) => page.locator(".session-chip", { hasText: name });

test("reloading a session with a stuck save lands the text it was holding", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "stuck-work");
  const worktree = sessionWorktrees(weavie.workspace)[0];
  const stuck = join(worktree, "notes.txt");
  const held = join(worktree, "hello.ts");
  const original = await readFile(held, "utf8");
  await openFile(page, "hello.ts");
  await openFile(page, "notes.txt");
  const server = await jamWrites(stuck);
  await clickIntoEditor(page);
  await typeInEditor(page, "STUCK ");
  const bar = page.locator(".connection-banner", { hasText: "needs a reload" });
  await expect(bar).toContainText("Saving a file");

  // hello.ts's save queues behind the stuck write, so its text can only reach disk through the reload.
  await page.locator(".editor-tab", { hasText: "hello.ts" }).click();
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /hello\.ts$/);
  await page.evaluate(() => window.__WEAVIE_EDITOR__?.focus());
  await pressDocumentStart(page);
  await typeInEditor(page, "CARRIED ");
  await expect(
    page.locator(".editor-tab", { hasText: "hello.ts" }).locator(".editor-tab-dirty"),
  ).toBeVisible();
  expect(await readFile(held, "utf8")).toBe(original);
  await submitAcpDraft(surface, "still answering");
  await expect(surface).toContainText("echo: still answering");

  // A healthy notes.txt lets the restore finish; the stuck write stays on the orphaned FIFO until the host exits.
  process.kill(-(server.pid as number), "SIGKILL");
  const orphan = `${stuck}.orphan`;
  execFileSync("ln", [stuck, orphan]);
  await writeFile(`${stuck}.fresh`, "");
  await rename(`${stuck}.fresh`, stuck);

  await bar.getByRole("button", { name: "Reload" }).click();
  await expect(page.getByRole("dialog", { name: "Reload this session?" })).toBeVisible();
  await page.keyboard.press("Enter");

  await expect(bar).toBeHidden();
  await expect.poll(() => readFile(held, "utf8")).toBe(`CARRIED ${original}`);
  await expect(page.locator(".editor-tab .editor-tab-dirty")).toHaveCount(0);
  await expect(surface).toContainText("echo: still answering");
  await submitAcpDraft(surface, "after the reload");
  await expect(surface).toContainText("echo: after the reload");

  // The retired working copy must not hold session switching hostage.
  await sessionChip(page, "main").click();
  await expect(sessionChip(page, "main")).toHaveClass(/active/);

  closeSync(openSync(orphan, constants.O_RDONLY | constants.O_NONBLOCK));
});

test("a stuck session can be left, and a refresh keeps its unsaved text", async ({
  page,
  weavie,
}) => {
  await createAcpSession(page, "stuck-work");
  const stuck = join(sessionWorktrees(weavie.workspace)[0], "notes.txt");
  await openFile(page, "notes.txt");
  const server = await jamWrites(stuck);
  await clickIntoEditor(page);
  await typeInEditor(page, "STUCK ");
  const bar = page.locator(".connection-banner", { hasText: "needs a reload" });
  await expect(bar).toContainText("Saving a file");
  await typeInEditor(page, "LATER ");

  await sessionChip(page, "main").click();
  await expect(sessionChip(page, "main")).toHaveClass(/active/);
  await expect(sessionChip(page, "stuck work").locator(".session-status")).toHaveClass(
    /status-error/,
  );
  await sessionChip(page, "stuck work").click();
  await expect(sessionChip(page, "stuck work")).toHaveClass(/active/);

  await page.reload();
  await expect(bar).toBeVisible();

  process.kill(-(server.pid as number), "SIGKILL");
  const orphan = `${stuck}.orphan`;
  execFileSync("ln", [stuck, orphan]);
  await writeFile(`${stuck}.fresh`, "");
  await rename(`${stuck}.fresh`, stuck);

  await bar.getByRole("button", { name: "Reload" }).click();
  await page.keyboard.press("Enter");
  await expect(bar).toBeHidden();
  await expect.poll(() => readFile(stuck, "utf8")).toContain("LATER");

  closeSync(openSync(orphan, constants.O_RDONLY | constants.O_NONBLOCK));
});
