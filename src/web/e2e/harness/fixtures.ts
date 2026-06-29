import { test as base, expect } from "@playwright/test";
import { fakeClaudeBuilt } from "./fake-claude";
import { type WeavieHost, headlessBuilt, launchHeadless } from "./weavie-host";
import { launchRemote, runnerBuilt } from "./weavie-runner";

// Per-test options. `fakeScript` (set via test.use) seeds the fake claude before the host boots, so MCP/
// hook-driven journeys have their script in place when the claude pane launches. Wrapped in an object
// because Playwright mangles a bare top-level array option value into [value, config].
type WeavieOptions = {
  fakeScript: { steps: import("./fake-claude").FakeStep[] } | null;
  // Set via test.use to boot the Open-PR scenario: a base+head git workspace and a stubbed PR provider.
  prScenario: boolean;
  // Set via test.use to stub the source connector with a canned Notion doc (WEAVIE_FAKE_NOTION), so a
  // notion.so open-target fetches + renders it deterministically. Null in normal use.
  notionDoc: { title: string; text: string; html: string } | null;
};

type WeavieFixtures = {
  weavie: WeavieHost;
};

// Transport is the project name: `headless` (browser → WSS → Weavie.Headless) or `remote` (browser → WSS
// → Weavie.Runner → spawned worker). The same journey runs on either; see the coverage matrix in
// docs/specs/integration-testing-strategy.md.
// `weavie` is an auto fixture: every functional test boots a host and navigates the page, whether or not it
// destructures the handle. Tests that need the host (workspace path, log) just add `weavie` to their args.
export const test = base.extend<WeavieOptions & WeavieFixtures>({
  fakeScript: [null, { option: true }],
  prScenario: [false, { option: true }],
  notionDoc: [null, { option: true }],
  weavie: [
    async ({ page, fakeScript, prScenario, notionDoc }, use, testInfo) => {
      const remote = testInfo.project.name === "remote";
      // Fail LOUDLY when a prerequisite host isn't built — never silently skip, which hides a broken build
      // (e.g. a failed `dotnet build`) as a green-looking run. A missing host is a setup error, not a pass.
      if (!headlessBuilt()) {
        throw new Error("Weavie.Headless not built — run: dotnet build src/Weavie.Headless");
      }
      if (!fakeClaudeBuilt()) {
        throw new Error("Weavie.FakeClaude not built — run: dotnet build tools/Weavie.FakeClaude");
      }
      if (remote && !runnerBuilt()) {
        throw new Error("Weavie.Runner not built — run: dotnet build src/Weavie.Runner");
      }

      const host = await (remote ? launchRemote : launchHeadless)({
        fakeScript: fakeScript?.steps ?? null,
        pr: prScenario,
        notionDoc: notionDoc ?? undefined,
      });
      await page.goto(host.url, { waitUntil: "domcontentloaded" });
      // The app removes the splash element once it has booted (layout + first session). Its disappearance
      // is the "app is interactive" signal — not a fixed sleep.
      await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
      await use(host);
      await host.stop();
    },
    { auto: true },
  ],
});

export { expect };
