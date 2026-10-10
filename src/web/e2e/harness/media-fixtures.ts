import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { crc32, deflateSync } from "node:zlib";
import type { Locator } from "@playwright/test";
import type { WorkspaceSeed } from "./weavie-host";

// A 640×360 (16:9) 20s VP9 clip: a landscape video long enough to toggle playback mid-test.
const LANDSCAPE_WEBM = Buffer.from(
  "GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQJChYECGFOAZwEAAAAAAATREU2bdLpNu4tTq4QVSalmU6yBoU27i1OrhBZUrmtTrIHWTbuMU6uEElTDZ1OsggElTbuMU6uEHFO7a1OsggS77AEAAAAAAABZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmsCrXsYMPQkBNgIxMYXZmNjEuNy4xMDNXQYxMYXZmNjEuNy4xMDNEiYhA04gAAAAAABZUrmvKrgEAAAAAAABB14EBc8WITwYvpxVpXCucgQAitZyDdW5kiIEAhoVWX1ZQOYOBASPjg4Q7msoA4JKwggKAuoIBaJqBAlWwhFW5gQESVMNnQH9zc59jwIBnyJlFo4dFTkNPREVSRIeMTGF2ZjYxLjcuMTAzc3PaY8CLY8WITwYvpxVpXCtnyKVFo4dFTkNPREVSRIeYTGF2YzYxLjE5LjEwMSBsaWJ2cHgtdnA5Z8ihRaOIRFVSQVRJT05Eh5MwMDowMDoyMC4wMDAwMDAwMDAAH0O2dUEu54EAo0CHgQAAgIJJg0IAJ/AWdgA4JBwZcBAAiGIMw/oq+keH/RV7QC5Z7gAAAAAAMmb2a/sa0OXhN7Jgu1uJJO4OQg+jicmA+EH0czNnKjiOFNZhHzUlAc8PhCxCGgQh4dAAZxzQJBG8FQwBw7HFC0fecJU45/BR6dLoE+sROef3U1P4JvTHg6Z50ytQo5+BA+gAhgBAkhwAX+EAAgBg1YAAAAAABFpnF1BaZxdQo56BB9AAhgBAkhwAXwEAAYBgAAAAAAAEWmcXUFpnF1CjnoELuACGAECSHABf4QABgGAAAAAAAARaZxdQWmcXUKOegQ+gAIYAQJIcAF3BAAGAYAAAAAAABFpnF1BaZxdQo56BE4gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1AfQ7Z1QMTnghdwo56BAAAAhgBAkhwAXwEAAYBgAAAAAAAEWmcXUFpnF1CjnoED6ACGAECSHABf4QABgGAAAAAAAARaZxdQWmcXUKOegQfQAIYAQJIcAFuBAAGAYAAAAAAABFpnF1BaZxdQo56BC7gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1CjnoEPoACGAMCSHABbgQABgGAAAAAAAARaZxdQWmcXUKOegROIAIYAQJIcAF/hAAGAYAAAAAAABFpnF1BaZxdQH0O2dUDE54Iu4KOegQAAAIYAQJIcAF3BAAGAYAAAAAAABFpnF1BaZxdQo56BA+gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1CjnoEH0ACGAECSHABfAQABgGAAAAAAAARaZxdQWmcXUKOegQu4AIYAQJIcAF/hAAGAYAAAAAAABFpnF1BaZxdQo56BD6AAhgBAkhwAW4EAAYBgAAAAAAAEWmcXUFpnF1CjnoETiACGAECSHABf4QABgGAAAAAAAARaZxdQWmcXUB9DtnXE54JGUKOegQAAAIYAQJIcAF8BAAGAYAAAAAAABFpnF1BaZxdQo56BA+gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1AcU7trkbuPs4EAt4r3gQHxggGq8IED",
  "base64",
);

// A 1600×800 RGB PNG, red left quarter on blue: larger than any pane, so it renders fitted, and the band
// shows which way it's turned.
function landscapePng(width: number, height: number): Buffer {
  const chunk = (type: string, data: Buffer): Buffer => {
    const body = Buffer.concat([Buffer.from(type), data]);
    const out = Buffer.alloc(body.length + 8);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc32(body), body.length + 4);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8);
  const row = Buffer.alloc(1 + width * 3);
  for (let x = 0; x < width; x += 1) {
    row.set(x < width / 4 ? [224, 64, 48] : [32, 96, 192], 1 + x * 3);
  }
  const pixels = deflateSync(Buffer.concat(Array.from({ length: height }, () => row)));
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk("IHDR", header),
    chunk("IDAT", pixels),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

/** Seeds landscape.png (1600×800) and landscape.webm (640×360) into the workspace. */
export const landscapeMedia: WorkspaceSeed = {
  run: async (workspace) => {
    await writeFile(join(workspace, "landscape.png"), landscapePng(1600, 800));
    await writeFile(join(workspace, "landscape.webm"), LANDSCAPE_WEBM);
  },
};

/** The media pane's live transform as a matrix (identity at the fitted, unrotated view). */
export function zoomMatrix(pane: Locator): Promise<{ a: number; b: number; e: number; f: number }> {
  return pane.locator(".editor-media-zoom").evaluate((element) => {
    const { a, b, e, f } = new DOMMatrix(getComputedStyle(element).transform);
    return { a, b, e, f };
  });
}

/**
 * The rendered (transformed) media box against the pane. A quarter turn fits it inside the pane, filling one
 * axis unless that would enlarge it past its natural size (`natural`: its longer side is at natural size).
 */
export async function renderedFit(pane: Locator): Promise<{
  portrait: boolean;
  fillsAnAxis: boolean;
  fitsInside: boolean;
  natural: boolean;
}> {
  const content = pane.locator(".editor-media-content");
  const [outer, inner, naturalLong] = await Promise.all([
    pane.boundingBox(),
    content.boundingBox(),
    content.evaluate((element) =>
      element instanceof HTMLVideoElement
        ? Math.max(element.videoWidth, element.videoHeight)
        : Math.max(
            (element as HTMLImageElement).naturalWidth,
            (element as HTMLImageElement).naturalHeight,
          ),
    ),
  ]);
  if (outer === null || inner === null) throw new Error("media pane or content is not rendered");
  return {
    portrait: inner.height > inner.width,
    fillsAnAxis:
      Math.abs(inner.width - outer.width) < 1.5 || Math.abs(inner.height - outer.height) < 1.5,
    fitsInside: inner.width <= outer.width + 1.5 && inner.height <= outer.height + 1.5,
    natural: Math.abs(Math.max(inner.width, inner.height) - naturalLong) < 1.5,
  };
}
