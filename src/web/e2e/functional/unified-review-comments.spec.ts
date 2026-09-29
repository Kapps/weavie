import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.use({ prScenario: true });

test("comment drafts and thread placement survive editor retirement and tab restore", async ({
  page,
}) => {
  test.slow();
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible();
  await page.locator(".editor-review-open").click();
  const hello = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: "hello.ts" }),
  });
  const body = hello.locator(".review-adaptive-body");
  await expect(body).toHaveAttribute("aria-busy", "false");
  await expect(body).toHaveAttribute("data-presentation", "passive");
  const thread = hello.locator(".weavie-pr-thread");
  await expect(thread.locator(".weavie-pr-comment-body")).toHaveText("Why change this greeting?");
  const draft = thread.locator(".weavie-pr-composer-input");
  await draft.fill("Draft retained across editing handoff.");
  const node = await draft.elementHandle();
  await expect(body).toHaveAttribute("data-presentation", "passive");
  await hello
    .locator(".view-line:visible", { hasText: "Hi there" })
    .click({ position: { x: 50, y: 8 } });
  await expect(body).toHaveAttribute("data-presentation", "live");
  await expect(draft).toHaveValue("Draft retained across editing handoff.");
  expect(await draft.evaluate((element, original) => element === original, node)).toBe(true);

  const vertical = page.getByRole("scrollbar", { name: "Review scroll position", exact: true });
  await vertical.press("Home");
  await page.locator(".unified-review-tree-row.file", { hasText: "feature.ts" }).click();
  await expect(body).toHaveAttribute("data-presentation", "passive");
  await vertical.press("End");
  await expect(thread).toBeInViewport();
  await expect(draft).toHaveValue("Draft retained across editing handoff.");
  expect(await draft.evaluate((element, original) => element === original, node)).toBe(true);

  await openFile(page, "README.md");
  await page.locator(".editor-tab", { hasText: "Review Changes" }).click();
  await vertical.press("End");
  await expect(draft).toHaveValue("Draft retained across editing handoff.");
  await thread.locator(".weavie-pr-composer-submit").click();
  await expect(
    thread.locator(".weavie-pr-comment-body", {
      hasText: "Draft retained across editing handoff.",
    }),
  ).toBeVisible();
  await expect(draft).toHaveValue("");
});
