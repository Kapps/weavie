import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { awaitEditorReady, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Opening a file that lives outside the session's worktree, full stack: the omnibar completes a typed absolute
// path against the host's real filesystem, the editor binds the file, and the footer states which capabilities
// are off for it. Nothing here is transport-sensitive, so it runs on headless only.

test("an absolute path opens a file outside the worktree, and the footer says what is off", async ({
  page,
}) => {
  await awaitEditorReady(page);

  const outsideDir = await mkdtemp(join(tmpdir(), "weavie-outside-"));
  try {
    const outside = join(outsideDir, "outside-notes.md");
    await writeFile(outside, "# Outside the worktree\n");

    const omnibar = page.locator(".tb-omnibar input");
    await omnibar.click();
    await omnibar.fill(outside);

    // The row source is the host's directory listing, so an entry appearing at all proves the host listed a
    // directory outside the worktree.
    const row = page.locator(".tb-omnibar-row", { hasText: "outside-notes.md" });
    await expect(row).toBeVisible();
    await row.click();

    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /outside-notes\.md$/);

    const chip = page.locator(".pane-footer .footer-outside-repo");
    await expect(chip).toBeVisible();
    await expect(chip).toHaveAttribute("title", /Blame, history and diff-against are unavailable/);

    // The false-positive guard: a file inside the worktree must not be flagged.
    await openFile(page, "hello.ts");
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /hello\.ts$/);
    await expect(chip).toBeHidden();
  } finally {
    await rm(outsideDir, { recursive: true, force: true });
  }
});

// The other half of opening a file from outside: nothing watched it, so an edit made elsewhere never reached
// the buffer and autosave could write over it. The workspace watcher is recursive over the worktree and stays
// that way, so an outside file gets its own watch for as long as it is open.
test("an edit made outside Weavie reaches a buffer opened from outside the worktree", async ({
  page,
}) => {
  await awaitEditorReady(page);

  const outsideDir = await mkdtemp(join(tmpdir(), "weavie-outside-"));
  try {
    const outside = join(outsideDir, "watched-notes.md");
    await writeFile(outside, "# Before\n");

    const omnibar = page.locator(".tb-omnibar input");
    await omnibar.click();
    await omnibar.fill(outside);
    await page.locator(".tb-omnibar-row", { hasText: "watched-notes.md" }).click();
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /watched-notes\.md$/);

    const marker = `external-edit-${Date.now()}`;
    await writeFile(outside, `# Before\n${marker}\n`);

    const modelText = () =>
      page.evaluate(
        () =>
          (
            window as Window & { __WEAVIE_EDITOR__?: { getModel(): { getValue(): string } | null } }
          ).__WEAVIE_EDITOR__
            ?.getModel()
            ?.getValue() ?? null,
      );
    await expect.poll(modelText).toContain(marker);
  } finally {
    await rm(outsideDir, { recursive: true, force: true });
  }
});
