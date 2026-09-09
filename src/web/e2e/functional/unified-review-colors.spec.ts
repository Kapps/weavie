import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

test.use({
  colorScheme: "dark",
  fakeScript: {
    steps: [
      {
        op: "edit",
        path: "{{WORKSPACE}}/comments.ts",
        content: "// surrounding context\n// Handle the previous case.\n// more context\n",
      },
      ...appliedEdit(
        "comments.ts",
        "// surrounding context\n// Handle the updated case.\n// more context\n",
      ),
    ],
  },
});

test("dark-mode comments remain readable over stacked added-line and word backgrounds", async ({
  page,
}) => {
  await awaitReviewSet(page, ["comments.ts"]);
  await page.locator(".editor-empty-review").click();
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "dark");
  const section = page.locator(".unified-review-file");
  const comment = section.locator(".view-line", { hasText: "Handle the updated case." });
  await expect(comment).toBeVisible();
  await expect(section.locator(".weavie-inline-added-text")).toBeVisible();
  await expect(comment.locator(".mtki")).toBeVisible();
  await expect
    .poll(() =>
      section.evaluate((element) => {
        const color = (selector: string, property: "color" | "backgroundColor"): string => {
          const target = element.querySelector(selector);
          if (target === null) throw new Error(`Missing review color layer: ${selector}`);
          return getComputedStyle(target)[property];
        };
        const canvas = document.createElement("canvas");
        canvas.width = canvas.height = 1;
        const context = canvas.getContext("2d");
        if (context === null) throw new Error("Cannot measure rendered review colors");
        const paint = (value: string): void => {
          context.fillStyle = value;
          context.fillRect(0, 0, 1, 1);
        };
        const luminance = (): number => {
          const rgb = [...context.getImageData(0, 0, 1, 1).data].slice(0, 3).map((channel) => {
            const srgb = channel / 255;
            return srgb <= 0.04045 ? srgb / 12.92 : ((srgb + 0.055) / 1.055) ** 2.4;
          });
          return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
        };
        paint(color(".monaco-editor-background", "backgroundColor"));
        paint(color(".weavie-inline-added", "backgroundColor"));
        paint(color(".weavie-inline-added-text", "backgroundColor"));
        const background = luminance();
        paint(color(".view-line .mtki", "color"));
        const foreground = luminance();
        return (
          (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05)
        );
      }),
    )
    .toBeGreaterThanOrEqual(4.5);
});
