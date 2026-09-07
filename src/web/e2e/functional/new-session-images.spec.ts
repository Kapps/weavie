import { expect, test } from "../harness/fixtures";
import { oversizedPng, pastePng } from "../harness/pasted-image";

test("new-session paste resizes an oversized image and shows invalid image failures", async ({
  page,
}) => {
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  const prompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
  const input = await oversizedPng(page);
  expect(Buffer.from(input, "base64").length).toBeGreaterThan(5 * 1024 * 1024);
  await pastePng(prompt, input);
  const attachment = inbox.locator(".agent-attachment");
  await expect(attachment).toHaveAttribute("title", "ready");
  const preview = attachment.locator("img");
  const encoded = (await preview.getAttribute("src")) as string;
  expect(encoded).toMatch(/^data:image\/png;base64,/);
  expect(Buffer.from(encoded.split(",")[1], "base64").length).toBeLessThan(5 * 1024 * 1024);
  await expect
    .poll(() => preview.evaluate((element) => (element as HTMLImageElement).naturalWidth))
    .toBeGreaterThan(0);
  expect(
    await preview.evaluate((element) => (element as HTMLImageElement).naturalWidth),
  ).toBeLessThan(1600);
  const dimensions = await preview.evaluate((element) => {
    const image = element as HTMLImageElement;
    const canvas = document.createElement("canvas");
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    const context = canvas.getContext("2d");
    if (context === null) throw new Error("Canvas is unavailable");
    context.drawImage(image, 0, 0);
    return {
      width: canvas.width,
      height: canvas.height,
      alpha: context.getImageData(0, 0, 1, 1).data[3],
    };
  });
  expect(Math.abs(dimensions.width * 1200 - dimensions.height * 1600)).toBeLessThanOrEqual(1600);
  expect(dimensions.alpha).toBeGreaterThan(0);
  expect(dimensions.alpha).toBeLessThan(255);
  await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill("fix/image-task");
  await expect(inbox.getByRole("button", { name: "Start", exact: true })).toBeEnabled();
  await attachment.getByTitle("Remove attachment").click();

  await pastePng(prompt, Buffer.alloc(5 * 1024 * 1024, 42).toString("base64"));
  await expect(attachment).toHaveClass(/failed/);
  await expect(inbox.getByRole("alert")).toBeVisible();
  await expect(inbox.getByRole("button", { name: "Start", exact: true })).toBeDisabled();
  await attachment.getByTitle("Remove attachment").click();
  await expect(inbox.getByRole("alert")).toHaveCount(0);
});
