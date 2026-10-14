import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { scrollReview } from "../harness/review-scroll";

// Scattered one-line edits give each file twenty hunks, each with its own context and gap band, so its real height
// is several screens past the estimate its section reserves before its editor reports one.
const FILES = 6;
const names = Array.from({ length: FILES }, (_, file) => `scattered${file}.ts`);
const base = (file: number): string[] =>
  Array.from({ length: 300 }, (_, line) => `export const f${file}_${line} = ${line};`);
const edited = (file: number): string =>
  base(file)
    .map((text, line) => (line % 15 === 7 ? text.replace(/;$/, " + 1;") : text))
    .join("\n");

const slow = new WeakMap<Page, (on: boolean) => void>();

test.use({
  workspaceSeed: {
    run: async (workspace: string) => {
      await Promise.all(
        names.map((name, file) => writeFile(join(workspace, name), base(file).join("\n"))),
      );
      execFileSync("git", ["add", "-A"], { cwd: workspace });
      execFileSync("git", ["commit", "-q", "-m", "baseline"], { cwd: workspace });
    },
  },
  fakeScript: { steps: names.flatMap((name, file) => appliedEdit(name, edited(file))) },
  preNavigate: {
    // A slow link: every host message arrives late, in order, so an editor reports its height after its section
    // first measured against the estimate — the late height that used to jump the view while scrolling up.
    async run(page) {
      let delay = 0;
      slow.set(page, (on) => {
        delay = on ? 300 : 0;
      });
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        server.onMessage((data) => {
          if (delay === 0) socket.send(data);
          else setTimeout(() => socket.send(data), delay);
        });
      });
    },
  },
});

test("scrolling up into files whose heights arrive late keeps the visible lines in place", async ({
  page,
}) => {
  await page.locator(".editor-empty-review").click();
  await expect(page.locator(".unified-review-file .view-line").first()).toBeVisible();
  await scrollReview(page, "end");
  await expect(page.locator(".unified-review-file .view-line").first()).toBeVisible();
  const diffs = (await page.locator(".unified-review-diffs").boundingBox())!;
  await page.mouse.move(diffs.x + diffs.width / 2, diffs.y + diffs.height / 2);

  const track = await page.evaluateHandle(() => {
    const viewport = document.querySelector(".unified-review-diffs")!;
    const frames: Map<string, number>[] = [];
    let running = true;
    const sample = (): void => {
      if (!running) return;
      const box = viewport.getBoundingClientRect();
      const lines = new Map<string, number>();
      for (const line of document.querySelectorAll(".unified-review-file .view-line")) {
        const rect = line.getBoundingClientRect();
        if (rect.top >= box.top && rect.bottom <= box.bottom)
          lines.set(line.textContent!, rect.top);
      }
      frames.push(lines);
      requestAnimationFrame(sample);
    };
    requestAnimationFrame(sample);
    return {
      // The largest frame-to-frame move of a line on screen in both frames, and frames that share no line at all.
      finish: () => {
        running = false;
        let largest = 0;
        let teleports = 0;
        for (let i = 1; i < frames.length; i++) {
          const before = frames[i - 1]!;
          const after = frames[i]!;
          if (before.size === 0 || after.size === 0) continue;
          let shared = 0;
          for (const [text, top] of after) {
            const previous = before.get(text);
            if (previous === undefined) continue;
            shared++;
            largest = Math.max(largest, Math.abs(top - previous));
          }
          if (shared === 0) teleports++;
        }
        return { largest, teleports, frames: frames.length };
      },
    };
  });

  slow.get(page)!(true);
  for (let notch = 0; notch < 40; notch++) {
    await page.mouse.wheel(0, -120);
    await page.waitForTimeout(80);
  }
  await page.waitForTimeout(1_000);
  slow.get(page)!(false);
  const result = await track.evaluate((tracker) => tracker.finish());

  expect(result.frames).toBeGreaterThan(30);
  // A wheel glide moves lines well under a viewport per frame; a missed height correction moves them by the
  // whole correction, often off screen entirely.
  expect(result.teleports).toBe(0);
  expect(result.largest).toBeLessThan(200);
});
