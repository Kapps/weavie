import type { Page } from "@playwright/test";

export async function reviewScroll(page: Page): Promise<{ top: number; maximum: number }> {
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  return scrollbar.evaluate(readReviewScroll);
}

export function readReviewScroll(element: Element): { top: number; maximum: number } {
  return {
    top: Number(element.getAttribute("aria-valuenow")),
    maximum: Number(element.getAttribute("aria-valuemax")),
  };
}

export async function scrollReview(page: Page, target: "start" | "middle" | "end"): Promise<void> {
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  if (target !== "middle") {
    const focused = await page.evaluateHandle(() => document.activeElement as HTMLElement);
    await scrollbar.press(target === "start" ? "Home" : "End");
    await focused.evaluate((element) => element.focus({ preventScroll: true }));
    await focused.dispose();
    return;
  }
  const thumb = await scrollbar.locator(".slider").boundingBox();
  const track = await scrollbar.boundingBox();
  if (thumb === null || track === null) throw new Error("Review scrollbar is missing");
  await page.mouse.move(thumb.x + thumb.width / 2, thumb.y + thumb.height / 2);
  await page.mouse.down();
  await page.mouse.move(thumb.x + thumb.width / 2, track.y + track.height / 2);
  await page.mouse.up();
}
