import type { Locator, Page } from "@playwright/test";

export const reviewPaintSelector =
  '.review-adaptive-body[data-presentation="passive"] > .review-adaptive-passive, ' +
  '.review-adaptive-body[data-presentation="live"] > .review-adaptive-live';

export function reviewPaint(scope: Locator | Page): Locator {
  return scope.locator(reviewPaintSelector);
}

export function reviewEditor(scope: Locator | Page): Locator {
  return scope.locator(".unified-review-editor-viewport > .monaco-editor");
}
