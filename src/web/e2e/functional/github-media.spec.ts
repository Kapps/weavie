import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { CLIP_WEBM, ZOOM_IMAGE_SRC } from "../harness/git-workspace";

test.use({ prScenario: true });

const asset = (id: number): string =>
  `https://github.com/user-attachments/assets/00000000-0000-4000-8000-${String(id).padStart(12, "0")}`;
const imageBytes = Buffer.from(ZOOM_IMAGE_SRC.split(",")[1]!, "base64");
const conversation = (page: Page) =>
  page.locator('.session-overview-section[data-section="conversation"]');

async function postComment(page: Page, body: string): Promise<void> {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await runCommand(page, "Show Session Overview");
  await conversation(page)
    .getByRole("button", { name: /Comment on this pull request/ })
    .focus();
  await page.keyboard.press("Enter");
  const input = conversation(page).locator(".weavie-pr-composer-input");
  await input.fill(body);
  await input.press("ControlOrMeta+Enter");
  await expect(conversation(page).locator(".weavie-pr-composer")).toHaveCount(0);
}

test("GitHub attachment links decode inline, play, and release media when collapsed", async ({
  page,
}) => {
  await page.route(asset(1), (route) =>
    route.fulfill({ contentType: "image/png", body: imageBytes }),
  );
  await page.route(asset(2), (route) =>
    route.fulfill({ contentType: "video/webm", body: CLIP_WEBM }),
  );
  await postComment(page, `Attachment previews\n\n[Picture](${asset(1)})\n\n${asset(2)}`);
  const comment = conversation(page).locator(".session-overview-comment").last();
  const image = comment.locator(".github-media > img");
  const video = comment.locator(".github-media > video");
  await expect(image).toHaveJSProperty("naturalWidth", 200);
  await expect(video).toHaveJSProperty("videoWidth", 16);
  await expect(video).toHaveAttribute("controls", "");
  await expect(video).not.toHaveAttribute("autoplay");
  await expect(comment.getByRole("link", { name: "Picture", exact: true })).toHaveAttribute(
    "href",
    asset(1),
  );
  await expect(comment.getByRole("link", { name: asset(2), exact: true })).toHaveAttribute(
    "href",
    asset(2),
  );
  await video.scrollIntoViewIfNeeded();
  await video.focus();
  await page.keyboard.press("Space");
  await expect
    .poll(() => video.evaluate((element: HTMLVideoElement) => element.currentTime))
    .toBeGreaterThan(0);

  const retired = await video.elementHandle();
  await conversation(page).locator(".session-overview-heading").click();
  await expect(video).toHaveCount(0);
  expect(
    await retired!.evaluate(
      (element: HTMLVideoElement) => element.paused && !element.hasAttribute("src"),
    ),
  ).toBe(true);
  await conversation(page).locator(".session-overview-heading").click();
  await expect(image).toHaveJSProperty("naturalWidth", 200);
  await expect(video).toHaveJSProperty("videoWidth", 16);
  await expect(comment.locator(".github-media")).toHaveCount(2);
});

test("unavailable attachments report errors while other links and code stay untouched", async ({
  page,
}) => {
  const ordinary = "https://github.com/acme/demo/pull/101";
  const outside = "https://example.com/clip.mp4";
  const wrapped = "https://user-images.githubusercontent.com/123/picture.png";
  const requests: string[] = [];
  await page.route("https://github.com/**", (route) => {
    requests.push(route.request().url());
    return route.fulfill({
      status: route.request().url() === asset(4) ? 404 : 200,
      contentType: "text/plain",
      body: "This is not media",
    });
  });
  await page.route(outside, (route) => {
    requests.push(route.request().url());
    return route.abort();
  });
  await page.route(wrapped, (route) =>
    route.fulfill({ contentType: "image/png", body: imageBytes }),
  );
  await postComment(
    page,
    [
      "Unsupported attachments",
      asset(3),
      asset(4),
      ordinary,
      outside,
      `\`${asset(5)}\``,
      `\`\`\`\n${asset(6)}\n\`\`\``,
      `[![Already an image](${wrapped})](${wrapped})`,
    ].join("\n\n"),
  );
  const comment = conversation(page).locator(".session-overview-comment").last();
  await expect(comment.locator(".github-media-status")).toHaveText([
    /Attachment preview unavailable/,
    /Attachment preview unavailable/,
  ]);
  await expect(comment.locator(".github-media")).toHaveCount(2);
  await expect(comment.getByRole("link", { name: ordinary, exact: true })).toHaveAttribute(
    "href",
    ordinary,
  );
  await expect(comment.locator("a > img")).toHaveJSProperty("naturalWidth", 200);
  expect(new Set(requests)).toEqual(new Set([asset(3), asset(4)]));
});
