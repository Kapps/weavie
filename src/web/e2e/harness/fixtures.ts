import { writeFile } from "node:fs/promises";
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
  // notion.so open-target fetches + renders it deterministically. `truncated` shows the incomplete banner;
  // `rejectEdits` makes every source-save-edit conflict (the stale-edit UX). Null in normal use.
  notionDoc: {
    title: string;
    markdown: string;
    editedTime?: string;
    truncated?: boolean;
    rejectEdits?: boolean;
  } | null;
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
      // Collect the page's console errors/warnings for the failure dump: a browser-side error that disrupts
      // boot (e.g. a Windows `net::ERR_NO_BUFFER_SPACE` resource-load failure) is invisible in the DOM
      // snapshot but is the first thing needed to root-cause an editor/diff render timeout.
      const consoleErrors: string[] = [];
      page.on("console", (msg) => {
        // Errors only: a browser-level failure (a failed resource load, an uncaught exception) is the signal.
        // Warnings are dropped — the WebGL "GPU stall" perf warnings would bury it.
        if (msg.type() === "error") {
          consoleErrors.push(`[error] ${msg.text()}`);
        }
      });
      page.on("pageerror", (err) => consoleErrors.push(`[pageerror] ${String(err)}`));
      await page.goto(host.url, { waitUntil: "domcontentloaded" });
      // The app removes the splash element once it has booted (layout + first session). Its disappearance
      // is the "app is interactive" signal — not a fixed sleep.
      await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
      await use(host);
      // On failure, attach the host's captured stdout/stderr and the fake-claude log: a host crash is
      // invisible in the browser trace (the page just shows "Lost connection"), so the .NET exception that
      // killed it must ride the test artifacts. See issue #197.
      if (testInfo.status !== testInfo.expectedStatus) {
        // The viewport/layout state rides along too: one CI failure showed the whole app painting at 0.6
        // scale with the editor container at 5px CSS — invisible in a DOM snapshot, obvious in these numbers.
        const layout = await page
          .evaluate(() => {
            const rect = (sel: string) => {
              try {
                const el = document.querySelector(sel);
                if (!el) {
                  return "absent";
                }
                const r = el.getBoundingClientRect();
                return `${Math.round(r.width)}x${Math.round(r.height)}`;
              } catch {
                // A malformed/unsupported selector must not sink the whole probe — degrade this one field.
                return "selector-error";
              }
            };
            return JSON.stringify(
              {
                inner: `${window.innerWidth}x${window.innerHeight}`,
                dpr: window.devicePixelRatio,
                visualViewport: window.visualViewport
                  ? `${Math.round(window.visualViewport.width)}x${Math.round(window.visualViewport.height)} scale=${window.visualViewport.scale}`
                  : "absent",
                html: rect("html"),
                body: rect("body"),
                app: rect(".app"),
                appBody: rect(".app-body"),
                layoutRoot: rect(".layout-root"),
                // The editor pane chain, so a 0-height editor (the S3 5px collapse) is pinpointed to a
                // level: which of paneSlot -> editorSurface -> editorPane -> editor is the one that's 0-high.
                editorPaneSlot: rect(".layout-root > .pane-slot:has(.editor-surface)"),
                editorSurface: rect(".editor-surface"),
                editorPane: rect(".editor-surface .editor-pane"),
                editor: rect(".editor-surface .editor"),
                monaco: rect(".editor-surface .monaco-editor"),
                // The live review-walk file set: on a PR-switch failure this shows whether the navigator holds
                // a leaked cross-PR mix (a host push bug) or the correct set (a test-walk race).
                review: window.__WEAVIE_REVIEW__ ?? null,
              },
              null,
              2,
            );
          })
          .catch((err) => `layout probe failed: ${err}`);
        for (const [name, content] of [
          ["weavie-host.log", host.log()],
          ["fake-claude.log", host.fakeLog()],
          ["viewport-layout.json", layout],
          ["console-errors.txt", consoleErrors.join("\n") || "(none)"],
        ] as const) {
          const path = testInfo.outputPath(name);
          await writeFile(path, content);
          await testInfo.attach(name, { path, contentType: "text/plain" });
        }
      }
      await host.stop();
    },
    { auto: true },
  ],
});

export { expect };
