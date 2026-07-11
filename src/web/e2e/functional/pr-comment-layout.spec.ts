import type { Locator, Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { walkToChangedFile } from "../harness/navigator";

// PR comment-thread LAYOUT: zones must size to their real rendered content (addContentSizedZone), so a long
// wrapping body, replies, and the composer are never clipped and never grow an inner scrollbar. Pins the
// hardcoded-zone-height regression (Reply button cut off, un-scrollable overflow:auto thread).
test.use({ prScenario: true });

// A body long enough to wrap over several lines at the 1280px viewport — the case fixed zone heights clipped.
const LONG_BODY =
  "This greeting is asserted verbatim by three snapshot suites and scraped by the docs pipeline for the " +
  "quickstart banner, so changing the wording here silently breaks all of them on the next scheduled build. " +
  "Could we keep the original string, or at least stage the new copy behind the greeting feature flag until " +
  "the snapshots and the scraper fixtures have both been regenerated and merged?";

// The zone (the content's Monaco-sized wrapper) must be at least as tall as the content needs — margins
// included — with no inner scroll. Polled: the ResizeObserver sizes the zone a frame after render.
async function expectUnclipped(content: Locator): Promise<void> {
  await expect
    .poll(async () =>
      content.evaluate((el) => {
        const style = getComputedStyle(el);
        const wrapper = el.parentElement;
        if (!(el instanceof HTMLElement) || wrapper === null) {
          return "no wrapper";
        }
        const needed =
          el.offsetHeight + parseFloat(style.marginTop) + parseFloat(style.marginBottom);
        const zone = wrapper.getBoundingClientRect().height;
        if (style.overflowY !== "visible") {
          return `inner scroll container: overflow-y ${style.overflowY}`;
        }
        if (el.scrollHeight > el.clientHeight + 1) {
          return `content overflows: scrollHeight ${el.scrollHeight} > clientHeight ${el.clientHeight}`;
        }
        return zone >= needed - 1 ? "ok" : `zone ${zone}px < content ${needed}px`;
      }),
    )
    .toBe("ok");
}

// `inner` sits fully inside `outer`'s box (nothing hangs below a too-short zone).
async function expectWithin(inner: Locator, outer: Locator): Promise<void> {
  const [a, b] = [await inner.boundingBox(), await outer.boundingBox()];
  expect(a).not.toBeNull();
  expect(b).not.toBeNull();
  if (a && b) {
    expect(a.y).toBeGreaterThanOrEqual(b.y - 1);
    expect(a.y + a.height).toBeLessThanOrEqual(b.y + b.height + 1);
  }
}

async function openCommentedFile(page: Page): Promise<void> {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await walkToChangedFile(page, "hello.ts");
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" }),
  ).toBeVisible({ timeout: 10_000 });
}

test("a thread with a long wrapping comment and replies is fully visible and grows on reply", async ({
  page,
}) => {
  await openCommentedFile(page);
  const thread = page.locator(".weavie-pr-thread").first();
  const zone = () => page.locator(".weavie-pr-thread").first().locator("..");

  // A LONG wrapping body in the thread: post it as a reply (round-trips the store and re-renders the thread
  // through the same renderComments path as the initial load). It must render whole — body, then the Reply
  // composer with its button — with no clipping and no inner scrollbar.
  await thread.locator(".weavie-pr-composer-input").fill(LONG_BODY);
  await thread.locator(".weavie-pr-composer-submit").click();
  const longBody = page.locator(".weavie-pr-comment-body", { hasText: "snapshot suites" });
  await expect(longBody).toBeVisible({ timeout: 10_000 });
  await expectUnclipped(page.locator(".weavie-pr-thread").first());
  await expectWithin(longBody, zone());
  await expectWithin(page.locator(".weavie-pr-composer-submit"), zone());
  const heightAfterOneReply = (await zone().boundingBox())?.height ?? 0;

  // A second reply → root + 2 replies: every comment and the composer stay visible, and the zone GREW to fit.
  const rerendered = page.locator(".weavie-pr-thread").first();
  await rerendered.locator(".weavie-pr-composer-input").fill("Second reply, same thread.");
  await rerendered.locator(".weavie-pr-composer-submit").click();
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Second reply, same thread." }),
  ).toBeVisible({ timeout: 10_000 });
  await expect(page.locator(".weavie-pr-comment")).toHaveCount(3);
  await expectUnclipped(page.locator(".weavie-pr-thread").first());
  await expectWithin(page.locator(".weavie-pr-composer-submit"), zone());
  const grown = (await zone().boundingBox())?.height ?? 0;
  expect(grown).toBeGreaterThan(heightAfterOneReply + 10);
});

test("the toolbar Comment composer shows its textarea, Comment and Cancel in full", async ({
  page,
}) => {
  await openCommentedFile(page);

  await page.locator(".weavie-inline-comment").click();
  const composer = page.locator(".weavie-pr-thread-new");
  const zone = composer.locator("..");
  await expect(composer.locator(".weavie-pr-composer-input")).toBeVisible();
  await expect(composer.locator(".weavie-pr-composer-submit")).toHaveText("Comment");
  await expect(composer.locator(".weavie-pr-composer-cancel")).toBeVisible();
  await expectUnclipped(composer);
  await expectWithin(composer.locator(".weavie-pr-composer-submit"), zone);
  await expectWithin(composer.locator(".weavie-pr-composer-cancel"), zone);

  // Cancel (now in the composer row) closes the zone.
  await composer.locator(".weavie-pr-composer-cancel").click();
  await expect(page.locator(".weavie-pr-thread-new")).toHaveCount(0);
});
