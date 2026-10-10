import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator } from "@playwright/test";
import type { WorkspaceSeed } from "./weavie-host";

// A 640×360 (16:9) 20s VP9 clip: a landscape video long enough to toggle playback mid-test.
const LANDSCAPE_WEBM = Buffer.from(
  "GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQJChYECGFOAZwEAAAAAAATREU2bdLpNu4tTq4QVSalmU6yBoU27i1OrhBZUrmtTrIHWTbuMU6uEElTDZ1OsggElTbuMU6uEHFO7a1OsggS77AEAAAAAAABZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmsCrXsYMPQkBNgIxMYXZmNjEuNy4xMDNXQYxMYXZmNjEuNy4xMDNEiYhA04gAAAAAABZUrmvKrgEAAAAAAABB14EBc8WITwYvpxVpXCucgQAitZyDdW5kiIEAhoVWX1ZQOYOBASPjg4Q7msoA4JKwggKAuoIBaJqBAlWwhFW5gQESVMNnQH9zc59jwIBnyJlFo4dFTkNPREVSRIeMTGF2ZjYxLjcuMTAzc3PaY8CLY8WITwYvpxVpXCtnyKVFo4dFTkNPREVSRIeYTGF2YzYxLjE5LjEwMSBsaWJ2cHgtdnA5Z8ihRaOIRFVSQVRJT05Eh5MwMDowMDoyMC4wMDAwMDAwMDAAH0O2dUEu54EAo0CHgQAAgIJJg0IAJ/AWdgA4JBwZcBAAiGIMw/oq+keH/RV7QC5Z7gAAAAAAMmb2a/sa0OXhN7Jgu1uJJO4OQg+jicmA+EH0czNnKjiOFNZhHzUlAc8PhCxCGgQh4dAAZxzQJBG8FQwBw7HFC0fecJU45/BR6dLoE+sROef3U1P4JvTHg6Z50ytQo5+BA+gAhgBAkhwAX+EAAgBg1YAAAAAABFpnF1BaZxdQo56BB9AAhgBAkhwAXwEAAYBgAAAAAAAEWmcXUFpnF1CjnoELuACGAECSHABf4QABgGAAAAAAAARaZxdQWmcXUKOegQ+gAIYAQJIcAF3BAAGAYAAAAAAABFpnF1BaZxdQo56BE4gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1AfQ7Z1QMTnghdwo56BAAAAhgBAkhwAXwEAAYBgAAAAAAAEWmcXUFpnF1CjnoED6ACGAECSHABf4QABgGAAAAAAAARaZxdQWmcXUKOegQfQAIYAQJIcAFuBAAGAYAAAAAAABFpnF1BaZxdQo56BC7gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1CjnoEPoACGAMCSHABbgQABgGAAAAAAAARaZxdQWmcXUKOegROIAIYAQJIcAF/hAAGAYAAAAAAABFpnF1BaZxdQH0O2dUDE54Iu4KOegQAAAIYAQJIcAF3BAAGAYAAAAAAABFpnF1BaZxdQo56BA+gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1CjnoEH0ACGAECSHABfAQABgGAAAAAAAARaZxdQWmcXUKOegQu4AIYAQJIcAF/hAAGAYAAAAAAABFpnF1BaZxdQo56BD6AAhgBAkhwAW4EAAYBgAAAAAAAEWmcXUFpnF1CjnoETiACGAECSHABf4QABgGAAAAAAAARaZxdQWmcXUB9DtnXE54JGUKOegQAAAIYAQJIcAF8BAAGAYAAAAAAABFpnF1BaZxdQo56BA+gAhgBAkhwAX+EAAYBgAAAAAAAEWmcXUFpnF1AcU7trkbuPs4EAt4r3gQHxggGq8IED",
  "base64",
);

// A 1600×800 two-colour PNG, red left quarter on blue: larger than any pane, so it renders fitted, and the
// band shows which way it's turned.
const LANDSCAPE_PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAABkAAAAMgAQMAAABBDjwZAAAABlBMVEXgQDAgYMDdL+1SAAACi0lEQVR42u3PMQEAAAgDoPUvrRl26QENSGrzUkRERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERERETuLERsBp4QIMA2AAAAAElFTkSuQmCC",
  "base64",
);

/** Seeds landscape.png (1600×800) and landscape.webm (640×360) into the workspace. */
export const landscapeMedia: WorkspaceSeed = {
  run: async (workspace) => {
    await writeFile(join(workspace, "landscape.png"), LANDSCAPE_PNG);
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
