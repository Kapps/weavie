import { expect, test } from "@playwright/test";
import { headlessBuilt, launchHeadless } from "./harness/weavie-host";

// The ONE place the real claude runs: a gated canary for "did the claude CLI / hook contract change?".
// It asserts only that the app survives a real turn (the TUI paints through the whole stack), never on
// model content — that would be non-deterministic. Runs nightly in a credentialed environment, never on the
// PR path. See docs/specs/integration-testing-strategy.md.
test.describe("live smoke (real claude — gated)", () => {
  test.skip(
    !process.env.WEAVIE_LIVE_SMOKE,
    "gated: set WEAVIE_LIVE_SMOKE=1 in an environment with a logged-in claude",
  );

  test("the app survives one real claude turn", async ({ page }) => {
    test.skip(!headlessBuilt(), "Weavie.Headless not built (dotnet build src/Weavie.Headless)");

    const host = await launchHeadless({ fakeScript: null, realClaude: true });
    try {
      await page.goto(host.url, { waitUntil: "domcontentloaded" });
      await expect(page.locator("#splash")).toHaveCount(0, { timeout: 60_000 });

      // The real claude TUI painted into a pane — proof the whole stack (PTY → real CLI → render) is alive.
      await expect
        .poll(
          () =>
            page.evaluate(() =>
              Array.from(document.querySelectorAll(".xterm-rows")).some(
                (el) => (el.textContent ?? "").trim().length > 0,
              ),
            ),
          { timeout: 60_000 },
        )
        .toBe(true);
    } finally {
      await host.stop();
    }
  });
});
