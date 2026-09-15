import { awaitEditorReady, createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { transcriptGeometry } from "../harness/transcript-geometry";

test("precision scrolling and composer resizing preserve a paused reading position", async ({
  page,
}) => {
  await awaitEditorReady(page);
  await createSession(page, { branch: "scroll-follow", provider: "fake-acp" });
  const surface = page.locator('[data-surface="structured-agent"]');
  const composer = surface.locator("[data-agent-composer] textarea");
  const body = surface.locator(".agent-body");
  const distanceFromBottom = () =>
    body.evaluate(transcriptGeometry).then((geometry) => geometry.bottomDistance);

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
  const precision = await body.evaluate(async (element) => {
    await new Promise<void>((resolve) => setTimeout(resolve, 160));
    const rows = element.querySelector<HTMLElement>(".monaco-list-rows");
    if (rows === null) throw new Error("Missing transcript geometry");
    const offset = () => 0 - Number.parseFloat(rows.style.top);
    const start = offset();
    const scroll = (deltaY: number): number => {
      element.dispatchEvent(new WheelEvent("wheel", { deltaY, bubbles: true, cancelable: true }));
      return offset();
    };
    const forward = Array.from({ length: 4 }, () => scroll(-0.25)).at(-1)!;
    const reversed = Array.from({ length: 4 }, () => scroll(0.25)).at(-1)!;
    const integer = Array.from({ length: 8 }, () => scroll(-1)).at(-1)!;
    const largerInteger = Array.from({ length: 8 }, () => scroll(-2)).at(-1)!;
    const gesture = scroll(-32.5);
    await new Promise<void>((resolve) => setTimeout(resolve, 160));
    return {
      start,
      forward,
      reversed,
      integer,
      largerInteger,
      gesture,
      settled: offset(),
    };
  });
  expect(precision.forward).toBe(precision.start - 1);
  expect(precision.reversed).toBe(precision.start);
  expect(precision.integer).toBe(precision.start - 8);
  expect(precision.largerInteger).toBe(precision.integer - 16);
  expect(Math.abs(precision.gesture - (precision.largerInteger - 32.5))).toBeLessThanOrEqual(0.5);
  expect(precision.settled).toBe(precision.gesture);

  const anchor = await body.evaluate(async (element) => {
    const rows = element.querySelector<HTMLElement>(".monaco-list-rows");
    if (rows === null) throw new Error("Missing transcript geometry");
    const offset = () => 0 - Number.parseFloat(rows.style.top);
    let previous = offset();
    let stationaryFrames = 0;
    while (stationaryFrames < 2) {
      await new Promise<void>((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
      stationaryFrames = previous === offset() ? stationaryFrames + 1 : 0;
      previous = offset();
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
