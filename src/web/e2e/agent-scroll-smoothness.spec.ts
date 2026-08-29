import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost, mockSession } from "./mock-host";

// Scrolling back through history the pane has never measured used to stutter: the virtualizer answers each
// first measurement with a scroll correction, and resolving that correction against its own cached offset
// wrote back a position the live scroll had already moved past — one frame of motion discarded per newly
// measured row, smooth again only on a second pass over the same range. This drives the pane up at an exact
// constant rate and asserts the on-screen content tracks it while those corrections are still landing.

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

// Turns of prompt → collapsed tool activity → answer, so the range holds every entry shape the estimator
// guesses at, each guess wrong by a different amount.
const transcript = Array.from({ length: 150 }, (_, turn) => [
  {
    providerId: "acp",
    type: "user-message",
    turnId: `t${turn}`,
    itemId: `u-${turn}`,
    text: `Prompt ${turn}: please look into the thing and report back.`,
  },
  ...Array.from({ length: 4 }, (_, step) => ({
    providerId: "acp",
    type: "item-completed",
    turnId: `t${turn}`,
    itemId: `tool-${turn}-${step}`,
    itemType: "commandExecution",
    status: "completed",
    title: `grep -rn "thing" src/step${step}`,
    text: `src/file${step}.ts:${step * 7 + 3}: matched the thing`,
  })),
  {
    providerId: "acp",
    type: "item-completed",
    turnId: `t${turn}`,
    itemId: `a-${turn}`,
    itemType: "agentMessage",
    status: "completed",
    text: `### Answer ${turn}\n\n${Array.from(
      { length: (turn % 4) + 1 },
      (_, paragraph) =>
        `Paragraph ${paragraph + 1} of turn ${turn} with **formatted** content that wraps in the pane.`,
    ).join("\n\n")}`,
  },
]).flat();

test("scrolling back through never-measured history stays smooth", async ({ page }) => {
  const session = mockSession("scroll", "scroll", "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, { generation: 1, pageSize: 5000, messages: transcript });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const body = page.locator(".agent-body");
    await expect(body).toBeVisible();
    await expect(page.getByText("Answer 149", { exact: true })).toBeVisible({ timeout: 60_000 });
    await body.evaluate((element) => {
      element.scrollTo({ top: element.scrollHeight });
    });
    await page.waitForTimeout(1000);

    // Scroll up one exact step per frame. Every tracked row must move by exactly that step: the pane owes
    // the reader the motion it was given, whatever it is re-measuring underneath.
    const measured = await body.evaluate(async (element: HTMLElement) => {
      const rate = 8;
      const descriptor = Object.getOwnPropertyDescriptor(Element.prototype, "scrollTop");
      if (descriptor?.get === undefined || descriptor.set === undefined) {
        throw new Error("scrollTop is not an accessor");
      }
      const read = descriptor.get;
      const write = descriptor.set;
      let corrections = 0;
      let stepping = false;
      // Count every way the pane can move the scroll position, so the guard below stays about whether
      // corrections happened at all rather than which API applies them.
      Object.defineProperty(element, "scrollTop", {
        configurable: true,
        get(): number {
          return read.call(this) as number;
        },
        set(value: number) {
          if (!stepping) {
            corrections += 1;
          }
          write.call(this, value);
        },
      });
      const scrollTo = element.scrollTo.bind(element);
      element.scrollTo = ((options: ScrollToOptions) => {
        corrections += 1;
        scrollTo(options);
      }) as typeof element.scrollTo;
      const deviations: number[] = [];
      let previous: { id: string; top: number } | null = null;
      for (let frame = 0; frame < 500; frame++) {
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
        const viewportTop = element.getBoundingClientRect().top;
        let highest: { id: string; top: number } | null = null;
        for (const row of document.querySelectorAll<HTMLElement>(".agent-virtual-row")) {
          const id = row.dataset.transcriptEntry;
          const top = row.getBoundingClientRect().top - viewportTop;
          if (id !== undefined && top >= 0 && (highest === null || top < highest.top)) {
            highest = { id, top };
          }
        }
        if (highest !== null && previous?.id === highest.id) {
          deviations.push(highest.top - previous.top - rate);
        }
        previous = highest;
        stepping = true;
        element.scrollTop -= rate;
        stepping = false;
      }
      return {
        corrections,
        stutters: deviations.filter((deviation) => Math.abs(deviation) > 2).length,
      };
    });

    // The corrections have to be real, or a pane that simply stopped compensating would pass.
    expect(measured.corrections).toBeGreaterThan(10);
    expect(measured.stutters).toBe(0);
  } finally {
    await host.close();
  }
});

test("a wheel scroll does not resize the transcript under itself", async ({ page }) => {
  const session = mockSession("wheel", "wheel", "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, { generation: 1, pageSize: 5000, messages: transcript });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const body = page.locator(".agent-body");
    await expect(body).toBeVisible();
    await expect(page.getByText("Answer 149", { exact: true })).toBeVisible();
    await body.evaluate((element) => {
      element.scrollTo({ top: element.scrollHeight });
      element.scrollTop -= 200;
    });
    await expect(page.getByRole("button", { name: "Jump to latest" })).toBeVisible();

    const height = (): Promise<number> =>
      page.locator("[data-agent-transcript]").evaluate((element) => element.clientHeight);
    const bounds = (await body.boundingBox())!;
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);

    // A wheel notch is an animation toward a target offset. Whatever the pane learns about row sizes
    // while it is in flight must not move the content under it, or the animation is cancelled and the
    // notch delivers a fraction of the scroll it was given.
    const before = await height();
    await page.mouse.wheel(0, -600);
    const during = await page.evaluate(
      () =>
        new Promise<number>((resolve) =>
          requestAnimationFrame(() =>
            resolve(document.querySelector<HTMLElement>("[data-agent-transcript]")!.clientHeight),
          ),
        ),
    );
    expect(during).toBe(before);

    // Held, not dropped: the sizes still land once the scroll comes to rest.
    await expect.poll(height).not.toBe(before);
  } finally {
    await host.close();
  }
});
