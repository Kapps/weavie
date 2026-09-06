import { mkdir, rename, rmdir, symlink, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("@cross open file browser follows external directory and file changes", async ({
  page,
  weavie,
}) => {
  test.slow();
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
  await runCommand(page, "Toggle File Browser");
  await page.locator(".browser-row", { hasText: "empty-directory" }).click();
  await expect(page.locator(".browser-children .browser-empty")).toHaveText("Empty folder");
  await runCommand(page, "Toggle File Browser");
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
  let completedIndexes = 0;
  let failedListings = 0;
  let listingError = "";
  test.use({
    preNavigate: {
      run: async (page) => {
        page.on("websocket", (socket) =>
          socket.on("framereceived", ({ payload }) => {
            const message = JSON.parse(payload.toString());
            if (
              message.feature === "files" &&
              message.name === "index" &&
              message.payload.pending === false
            ) {
              completedIndexes += 1;
            }
            if (
              message.feature === "files" &&
              message.name === "listDirectory" &&
              typeof message.error === "string"
            ) {
              failedListings += 1;
              listingError = message.error;
            }
          }),
        );
      },
    },
  });

  test("a directory failure is distinct from empty and Retry requests a fresh listing", async ({
    page,
    weavie,
  }) => {
    const directory = join(weavie.home, "outside-workspace");
    const alias = join(weavie.workspace, "temporarily-missing");
    await mkdir(directory);
    await symlink(directory, alias, "junction");
    await runCommand(page, "Toggle File Browser");
    const row = page.locator(".browser-row", { hasText: "temporarily-missing" });
    await expect(row).toBeVisible();
    await rmdir(directory);
    await row.click();
    const error = page.locator(".browser-error");
    await expect(error).toBeVisible();
    expect(listingError).toContain(alias);
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
    await mkdir(directory);
    const previous = completedIndexes;
    await page.locator(".tb-omnibar-input").click();
    await page.locator(".tb-omnibar-input").fill("new-nested-match");
    await expect.poll(() => completedIndexes).toBeGreaterThan(previous);
    await writeFile(join(directory, "new-nested-match.txt"), "first child");
    await expect(
      page.locator(".tb-omnibar-row", { hasText: "new-nested-match.txt" }),
    ).toBeVisible();
  });
});
