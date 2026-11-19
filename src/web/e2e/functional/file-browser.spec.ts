import { mkdir, rename, rmdir, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import {
  activeSessionSlot,
  awaitEditorReady,
  createSession,
  runCommand,
  waitForSessionSwitch,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { gateFor, inboundGate } from "../harness/inbound-gate";
import { decodeTestWebSocketMessage, encodeTestWebSocketMessage } from "../harness/websocket-codec";

test("Filter Files opens on first use, searches collapsed paths and preserves the tree", async ({
  page,
  weavie,
}) => {
  await mkdir(join(weavie.workspace, "filter-nested/deeper"), { recursive: true });
  await mkdir(join(weavie.workspace, "filter-expanded"));
  await writeFile(join(weavie.workspace, "filter-nested/deeper/Needle.txt"), "nested match");
  await writeFile(join(weavie.workspace, "filter-expanded/retained.txt"), "expanded child");
  await awaitEditorReady(page);
  await expect(page.locator(".browser-panel")).toHaveCount(0);
  await runCommand(page, "Filter Files");
  const input = page.getByRole("combobox", { name: "Filter files by name or path" });
  const filter = page.getByRole("button", { name: "Filter", exact: true });
  const row = (name: string) => page.locator(".browser-row", { hasText: name });
  await expect(input).toBeFocused();
  await expect(filter).toHaveAttribute("title", /Ctrl\+Shift\+B|⌘\+Shift\+B/);
  await row("filter-expanded").click();
  await expect(row("retained.txt")).toBeVisible();
  await expect(row("Needle.txt")).toHaveCount(0);

  await input.fill("FILTER-NESTED/DEEPER");
  const match = page.locator(".browser-filter-result");
  await expect(match).toHaveCount(1);
  await expect(match).toContainText("Needle.txt");
  await expect(match).toContainText("filter-nested/deeper");
  await input.fill("nEeDlE");
  await expect(match).toHaveCount(1);
  await input.press("ArrowDown");
  await input.press("Enter");
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]Needle\.txt$/);

  await filter.click();
  await expect(input).toBeFocused();
  await input.fill("definitely-no-such-file");
  await expect(page.locator(".browser-empty", { hasText: "No matching files" })).toBeVisible();
  await expect(match).toHaveCount(0);
  await input.press("Escape");
  await expect(input).toHaveCount(0);
  await expect(filter).toBeFocused();
  await expect(row("retained.txt")).toBeVisible();
  await expect(row("Needle.txt")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(page.locator(".browser-panel")).toBeHidden();
  await runCommand(page, "Toggle File Browser");
  await expect(page.locator(".browser-panel")).toBeVisible();
  await expect(input).toHaveCount(0);
  await expect(filter).toHaveAttribute("aria-expanded", "false");
});

test("@cross open file browser follows external directory and file changes", async ({
  page,
  weavie,
}) => {
  const path = (name: string) => join(weavie.workspace, name);
  const row = (name: string) => page.locator(".browser-row", { hasText: name });
  await writeFile(path(".gitignore"), "live-folder/\nrenamed-folder/\n");
  await mkdir(path("live-folder"));
  await mkdir(path("unrelated-folder"));
  await writeFile(path("unrelated-folder/retained.txt"), "retained");
  await runCommand(page, "Toggle File Browser");
  await row("unrelated-folder").click();
  await expect(row("retained.txt")).toBeVisible();
  await row("live-folder").click();
  await expect(page.locator(".browser-children .browser-empty")).toHaveText("Empty folder");
  await expect(page.locator(".browser-children .browser-loading")).toHaveCount(0);

  await mkdir(path("new-empty-folder"));
  await expect(row("new-empty-folder")).toBeVisible();
  await rmdir(path("new-empty-folder"));
  await expect(row("new-empty-folder")).toHaveCount(0);
  await mkdir(path("live-folder/sub"));
  await writeFile(path("live-folder/sub/new-child.txt"), "child");
  await writeFile(path("new-root.txt"), "root");
  await row("sub").click();
  await expect(row("new-child.txt")).toBeVisible();
  await expect(row("new-root.txt")).toBeVisible();

  await rename(path("live-folder/sub/new-child.txt"), path("live-folder/sub/renamed-child.txt"));
  await rename(path("new-root.txt"), path("renamed-root.txt"));
  await expect(row("new-child.txt")).toHaveCount(0);
  await expect(row("new-root.txt")).toHaveCount(0);
  await expect(row("renamed-child.txt")).toBeVisible();
  await expect(row("renamed-root.txt")).toBeVisible();
  await rename(path("live-folder"), path("renamed-folder"));
  await expect(row("live-folder")).toHaveCount(0);
  await expect(row("renamed-folder")).toBeVisible();
  await expect(row("retained.txt")).toBeVisible();
  await mkdir(path("live-folder/sub"), { recursive: true });
  await row("live-folder").click();
  await row("sub").click();
  await expect(row("sub").locator("..").locator(".browser-empty")).toHaveText("Empty folder");
  await rmdir(path("live-folder/sub"));
  await rmdir(path("live-folder"));
  await expect(row("live-folder")).toHaveCount(0);
  await row("renamed-folder").click();
  await row("sub").click();
  await expect(row("renamed-child.txt")).toBeVisible();

  await unlink(path("renamed-folder/sub/renamed-child.txt"));
  await unlink(path("renamed-root.txt"));
  await expect(row("renamed-child.txt")).toHaveCount(0);
  await expect(row("renamed-root.txt")).toHaveCount(0);
  await expect(page.locator(".browser-children .browser-empty")).toHaveText("Empty folder");
  await expect(row("retained.txt")).toBeVisible();
});

test("an open Go to File query follows external create, rename and delete", async ({
  page,
  weavie,
}) => {
  const path = (name: string) => join(weavie.workspace, name);
  const row = (name: string) => page.locator(".tb-omnibar-row", { hasText: name });
  await mkdir(path("empty-directory"));
  await page.locator(".tb-omnibar-input").click();
  await page.locator(".tb-omnibar-input").fill("live-index");
  await expect(row("live-index")).toHaveCount(0);
  await writeFile(path("live-index-old.txt"), "new file");
  await expect(row("live-index-old.txt")).toBeVisible();
  await writeFile(path("empty-directory/live-index-nested.txt"), "first child");
  await expect(row("live-index-nested.txt")).toBeVisible();
  await rename(path("live-index-old.txt"), path("live-index-new.txt"));
  await expect(row("live-index-old.txt")).toHaveCount(0);
  await expect(row("live-index-new.txt")).toBeVisible();
  await unlink(path("live-index-new.txt"));
  await unlink(path("empty-directory/live-index-nested.txt"));
  await expect(row("live-index-new.txt")).toHaveCount(0);
  await expect(row("live-index-nested.txt")).toHaveCount(0);
  await expect(page.locator(".tb-omnibar-input")).toHaveValue("live-index");
});

test.describe("file request completion", () => {
  let indexChanges = 0;
  let failedListings = 0;
  let listingError = "";
  let failingDirectory = "";
  test.use({
    preNavigate: {
      run: async (page) => {
        await page.routeWebSocket("**/*", (socket) => {
          const server = socket.connectToServer();
          const listings = new Set<string>();
          socket.onMessage((data) => {
            const message = JSON.parse(decodeTestWebSocketMessage(data));
            if (
              message.feature === "files" &&
              message.name === "listDirectory" &&
              message.payload.path === failingDirectory
            )
              listings.add(message.requestId);
            server.send(data);
          });
          server.onMessage((data) => {
            const message = JSON.parse(decodeTestWebSocketMessage(data));
            if (message.feature === "files" && message.name === "indexChanged") indexChanges += 1;
            if (listings.delete(message.requestId)) {
              expect(message.error).toBeNull();
              message.error = `Cannot list directory: ${failingDirectory}`;
              listingError = message.error;
              failedListings += 1;
              socket.send(encodeTestWebSocketMessage(JSON.stringify(message)));
            } else socket.send(data);
          });
        });
      },
    },
  });

  test("a directory failure is distinct from empty and Retry requests a fresh listing", async ({
    page,
    weavie,
  }) => {
    const directory = join(weavie.workspace, "unreadable-directory");
    failingDirectory = directory;
    await mkdir(directory);
    await runCommand(page, "Toggle File Browser");
    await page.locator(".browser-row", { hasText: "unreadable-directory" }).click();
    const error = page.locator(".browser-error");
    await expect(error).toBeVisible();
    expect(listingError).toContain(directory);
    await expect(error.locator("span")).toHaveText(listingError);

    const previous = failedListings;
    await error.getByRole("button", { name: "Retry" }).click();
    await expect.poll(() => failedListings).toBeGreaterThan(previous);
    await expect(error.locator("span")).toHaveText(listingError);
    await expect(page.locator(".browser-children .browser-empty")).toHaveCount(0);
  });

  test("Go to File discovers the first child after indexing an empty directory", async ({
    page,
    weavie,
  }) => {
    const directory = join(weavie.workspace, "unbrowsed-empty");
    const previous = indexChanges;
    await mkdir(directory);
    await page.locator(".tb-omnibar-input").click();
    await page.locator(".tb-omnibar-input").fill("new-nested-match");
    // The host lists the empty directory, and so watches it, before the file arrives.
    await expect.poll(() => indexChanges).toBeGreaterThan(previous);
    await writeFile(join(directory, "new-nested-match.txt"), "first child");
    await expect(
      page.locator(".tb-omnibar-row", { hasText: "new-nested-match.txt" }),
    ).toBeVisible();
  });
});

// Filter Files belongs to the session it ran in. When that session leaves the screen while the docked browser is
// still being shown (its reply held here), the command says so and leaves the session now shown unfocused.
test.describe("a filter whose session leaves the screen", () => {
  test.use({ preNavigate: inboundGate });

  test("Filter Files declines without focusing the next session's browser", async ({ page }) => {
    const files = page.locator('.tool-panel[data-tool="files"]');
    const footerBranch = page.locator(
      '.terminal-surface[data-kind="terminal:shell"] .footer-branch-name',
    );
    const firstSlot = await activeSessionSlot(page);
    await createSession(page, { branch: "e2e/filter-owner", provider: "claude" });
    const secondSlot = await waitForSessionSwitch(page, firstSlot);
    await page.locator(`.session-chip[data-session-slot="${firstSlot}"]`).click();
    await expect(footerBranch).not.toHaveText("e2e/filter-owner");

    await page.keyboard.press("ControlOrMeta+b");
    await files.getByRole("button", { name: /^Stay Open/ }).click();
    await expect(files).not.toHaveClass(/tool-floating/);
    await page.keyboard.press("ControlOrMeta+b");
    await expect(files).toBeHidden();
    await page
      .locator(
        '.terminal-surface[data-kind="terminal:shell"] .term-host:not(.hidden) .xterm-screen',
      )
      .click();

    gateFor(page).hold();
    await runCommand(page, "Filter Files");
    await page.locator(`.session-chip[data-session-slot="${secondSlot}"]`).click();
    await expect(footerBranch).toHaveText("e2e/filter-owner");
    gateFor(page).release();

    await expect(
      page.locator(".toast", { hasText: "That session is no longer on screen." }),
    ).toBeVisible();
    await expect(files).toBeVisible();
    expect(
      await page.evaluate(() => document.activeElement?.closest('[data-tool="files"]') != null),
    ).toBe(false);
  });
});
