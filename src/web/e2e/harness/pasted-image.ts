import type { Locator, Page } from "@playwright/test";

/** Real, incompressible pixels force the browser to resize rather than discard PNG padding. */
export async function oversizedPng(page: Page): Promise<string> {
  return page.evaluate(() => {
    const canvas = document.createElement("canvas");
    canvas.width = 1600;
    canvas.height = 1200;
    const context = canvas.getContext("2d");
    if (context === null) throw new Error("Canvas is unavailable");
    const pixels = context.createImageData(canvas.width, canvas.height);
    let seed = 123456789;
    for (let i = 0; i < pixels.data.length; i++) {
      seed ^= seed << 13;
      seed ^= seed >>> 17;
      seed ^= seed << 5;
      pixels.data[i] = seed & 255;
    }
    context.putImageData(pixels, 0, 0);
    return canvas.toDataURL("image/png").split(",")[1];
  });
}

export async function pastePng(target: Locator, dataB64: string): Promise<void> {
  await target.evaluate((element, encoded) => {
    const bytes = Uint8Array.from(atob(encoded), (character) => character.charCodeAt(0));
    const clipboard = new DataTransfer();
    clipboard.items.add(new File([bytes], "image.png", { type: "image/png" }));
    element.dispatchEvent(
      new ClipboardEvent("paste", { clipboardData: clipboard, bubbles: true, cancelable: true }),
    );
  }, dataB64);
}
