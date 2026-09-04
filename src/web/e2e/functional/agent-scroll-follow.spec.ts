import { awaitEditorReady, createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("composer resizing preserves following latest and a paused reading position", async ({
  page,
}) => {
  await awaitEditorReady(page);
  await createSession(page, { branch: "scroll-follow", provider: "fake-acp" });
  const surface = page.locator('[data-surface="structured-agent"]');
  const composer = surface.locator("[data-agent-composer] textarea");
  const body = surface.locator(".agent-body");
  const distanceFromBottom = () =>
    body.evaluate((element) => element.scrollHeight - element.clientHeight - element.scrollTop);

  for (let turn = 0; turn < 8; turn++) {
    const text = `## Investigation ${turn}\n\n${Array.from(
      { length: (turn % 3) + 1 },
      (_, paragraph) =>
        `Finding ${paragraph + 1}: The transcript preserves the position of its formatted content as rows enter the viewport. This paragraph wraps naturally in the agent pane.`,
    ).join("\n\n")}`;
    await composer.fill(text);
    await expect.poll(distanceFromBottom).toBeLessThanOrEqual(1);
    await composer.press("Enter");
    const answer = surface.locator(".agent-entry-message.agent-tone-assistant").last();
    await expect(answer).toContainText(`Investigation ${turn}`);
    await expect(surface.getByRole("button", { name: "Run", exact: true })).toBeVisible();
    await expect(answer).toBeInViewport();
    await expect.poll(distanceFromBottom).toBeLessThanOrEqual(1);
  }

  await body.hover();
  await page.mouse.wheel(0, -720);
  await expect.poll(distanceFromBottom).toBeGreaterThan(500);
  const latest = surface.getByRole("button", { name: "Jump to latest", exact: true });
  await expect(latest).toBeVisible();
  const anchor = await body.evaluate(async (element) => {
    let previous = element.scrollTop;
    let stationaryFrames = 0;
    while (stationaryFrames < 2) {
      await new Promise<void>((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
      stationaryFrames = previous === element.scrollTop ? stationaryFrames + 1 : 0;
      previous = element.scrollTop;
    }
    const viewport = element.getBoundingClientRect();
    const row = Array.from(element.querySelectorAll<HTMLElement>(".agent-virtual-row")).find(
      (candidate) => candidate.getBoundingClientRect().bottom > viewport.top,
    );
    if (row === undefined) throw new Error("No visible transcript anchor");
    return {
      id: row.dataset.transcriptEntry!,
      top: row.getBoundingClientRect().top - viewport.top,
      height: element.clientHeight,
    };
  });

  await composer.fill(
    "An unsent draft\nwith several lines\nthat grows the composer\nand shrinks history\nwithout changing what I am reading.",
  );
  await expect
    .poll(() => body.evaluate((element) => element.clientHeight))
    .toBeLessThan(anchor.height);
  const displacement = await body.evaluate(async (element, previous) => {
    await new Promise<void>((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
    const row = Array.from(element.querySelectorAll<HTMLElement>(".agent-virtual-row")).find(
      (candidate) => candidate.dataset.transcriptEntry === previous.id,
    );
    if (row === undefined) throw new Error("Resizing the composer lost the reading position");
    return row.getBoundingClientRect().top - element.getBoundingClientRect().top - previous.top;
  }, anchor);
  expect(Math.abs(displacement)).toBeLessThanOrEqual(1);
  await expect(latest).toBeVisible();
});
