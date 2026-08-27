import { writeFile } from "node:fs/promises";
import { test as base, expect } from "@playwright/test";
import { type FakeInference, fakeClaudeBuilt } from "./fake-claude";
import { fakeAcpProgram, programExists } from "./test-programs";
import { headlessBuilt, launchHeadless, type WeavieHost } from "./weavie-host";
import { launchRemote, runnerBuilt } from "./weavie-runner";

// Per-test options. `fakeScript` (set via test.use) seeds the fake claude before the host boots, so MCP/
// hook-driven journeys have their script in place when the claude pane launches. Wrapped in an object
// because Playwright mangles a bare top-level array option value into [value, config].
type WeavieOptions = {
  fakeScript: { steps: import("./fake-claude").FakeStep[] } | null;
  inference: FakeInference;
  automaticInference: boolean;
  dismissInferenceOffer: boolean;
  // Set via test.use to run page setup (e.g. addInitScript recorders) BEFORE the fixture's first
  // navigation — a test-body addInitScript would need a second full app boot (reload) to apply. Wrapped
  // in an object because Playwright special-cases bare function/array option values.
  preNavigate: { run: (page: import("@playwright/test").Page) => Promise<void> } | null;
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
  inference: ["disabled", { option: true }],
  automaticInference: [false, { option: true }],
  dismissInferenceOffer: [true, { option: true }],
  preNavigate: [null, { option: true }],
  prScenario: [false, { option: true }],
  notionDoc: [null, { option: true }],
  weavie: [
    async (
      {
        page,
        fakeScript,
        inference,
        automaticInference,
        dismissInferenceOffer,
        preNavigate,
        prScenario,
        notionDoc,
      },
      use,
      testInfo,
    ) => {
      const remote = testInfo.project.name === "remote";
      // Fail LOUDLY when a prerequisite host isn't built — never silently skip, which hides a broken build
      // (e.g. a failed `dotnet build`) as a green-looking run. A missing host is a setup error, not a pass.
      if (!headlessBuilt()) {
        throw new Error("Weavie.Headless not built — run: dotnet build src/Weavie.Headless");
      }
      if (!fakeClaudeBuilt()) {
        throw new Error("Weavie.FakeClaude not built — run: dotnet build tools/Weavie.FakeClaude");
      }
      if (!programExists(fakeAcpProgram)) {
        throw new Error("Weavie.FakeAcp not built — run: dotnet build tools/Weavie.FakeAcp");
      }
      if (remote && !runnerBuilt()) {
        throw new Error("Weavie.Runner not built — run: dotnet build src/Weavie.Runner");
      }

      const host = await (remote ? launchRemote : launchHeadless)({
        fakeScript: fakeScript?.steps ?? null,
        inference,
        automaticInference,
        pr: prScenario,
        notionDoc: notionDoc ?? undefined,
      });
      // Collect the page's console errors for the failure dump: a browser-side error that disrupts boot
      // (e.g. a Windows `net::ERR_NO_BUFFER_SPACE` resource-load failure) is invisible in the DOM snapshot
      // but is the first thing needed to root-cause an editor/diff render timeout.
      const consoleErrors: string[] = [];
      page.on("console", (msg) => {
        // Errors only: a browser-level failure (a failed resource load, an uncaught exception) is the signal.
        // Warnings are dropped — the WebGL "GPU stall" perf warnings would bury it.
        if (msg.type() === "error") {
          consoleErrors.push(`[error] ${msg.text()}`);
        }
      });
      page.on("pageerror", (err) => consoleErrors.push(`[pageerror] ${String(err)}`));
      // A stylesheet or chunk that never arrives (Windows runners fail one with `net::ERR_NO_BUFFER_SPACE`
      // under socket pressure) leaves the app live but unstyled — `.app` loses its `height: 100%` and
      // collapses to content height, so panes render a few pixels tall and elements are present-but-hidden.
      // Every assertion after that fails somewhere unrelated, so record which load failed and say so.
      const blockedLoads: string[] = [];
      page.on("requestfailed", (request) => {
        const kind = request.resourceType();
        if (kind !== "stylesheet" && kind !== "script" && kind !== "document") {
          return;
        }
        const failure = `${kind} ${request.url()} — ${request.failure()?.errorText ?? "failed"}`;
        blockedLoads.push(failure);
        consoleErrors.push(`[requestfailed] ${failure}`);
      });
      const dumpDiagnostics = async (): Promise<void> => {
        const layout = await page
          .evaluate(() => {
            const rect = (selector: string): string => {
              try {
                const element = document.querySelector(selector);
                if (element === null) {
                  return "absent";
                }
                const bounds = element.getBoundingClientRect();
                return `${Math.round(bounds.width)}x${Math.round(bounds.height)}`;
              } catch {
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
                editorPaneSlot: rect(".layout-root > .pane-slot:has(.editor-surface)"),
                editorSurface: rect(".editor-surface"),
                editorPane: rect(".editor-surface .editor-pane"),
                editor: rect(".editor-surface .editor"),
                monaco: rect(".editor-surface .monaco-editor"),
                // A healthy rect with only one line rendered means the viewport recovered but the render
                // didn't — read scrollTop against contentHeight to see whether it's parked past the end.
                monacoViewportHeight: window.__WEAVIE_EDITOR__?.getLayoutInfo().height ?? null,
                // The offset the rects can't show: a viewport that recovers from the 5px clamp keeps the
                // scroll the collapse left behind, so it renders the file's tail (often one blank line).
                scrollTop: window.__WEAVIE_EDITOR__?.getScrollTop() ?? null,
                contentHeight: window.__WEAVIE_EDITOR__?.getContentHeight() ?? null,
                modelLineCount: window.__WEAVIE_EDITOR__?.getModel()?.getLineCount() ?? null,
                renderedLines: [...document.querySelectorAll(".view-line")].map((line) =>
                  (line.textContent ?? "").replace(/\s+/g, " "),
                ),
                review: window.__WEAVIE_REVIEW__ ?? null,
              },
              null,
              2,
            );
          })
          .catch((error) => `layout probe failed: ${error}`);
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
      };
      const teardown = async (): Promise<void> => {
        const failures: unknown[] = [];
        try {
          if (!page.isClosed()) {
            await page.close();
          }
        } catch (error) {
          failures.push(error);
        }
        try {
          await host.stop();
        } catch (error) {
          failures.push(error);
        }
        if (failures.length === 1) {
          throw failures[0];
        }
        if (failures.length > 1) {
          throw new AggregateError(failures, "Page and host teardown both failed.");
        }
      };
      const collectFailure = async (
        failures: unknown[],
        operation: () => Promise<void>,
      ): Promise<void> => {
        try {
          await operation();
        } catch (error) {
          failures.push(error);
        }
      };
      const throwFailures = (failures: unknown[], message: string): void => {
        if (failures.length === 1) {
          throw failures[0];
        }
        if (failures.length > 1) {
          throw new AggregateError(failures, message);
        }
      };

      try {
        if (preNavigate !== null) {
          await preNavigate.run(page);
        }
        const connect = await page.request.post(host.url, {
          form: { token: host.token },
          maxRedirects: 0,
        });
        if (connect.status() !== 302) {
          throw new Error(`workspace connect failed (${connect.status()})`);
        }
        // Windows CI runners occasionally drop one boot-time document/script/stylesheet load with
        // `net::ERR_NO_BUFFER_SPACE` — a transient Winsock buffer exhaustion on the runner, not an app
        // or test defect (see e912b8c, which added the blockedLoads diagnostic below). A full reload
        // re-requests every resource fresh, which clears a one-off OS hiccup; retried at most once so a
        // genuine break still fails loud via the existing diagnostic. Intercepting requests to retry the
        // single failed one (an earlier version of this fix) added a Node-side round trip to every
        // boot-time script/stylesheet — including the multi-MB monaco chunk — which pushed unrelated
        // boot-timing assertions (e.g. the automatic-inference offer) past their timeout on Linux CI; a
        // reload keeps the common path at its original, unproxied speed.
        // Flaked 2026-08-27 07:04 UTC on media.spec.ts "switching between a text tab and a media tab
        // keeps both healthy" (Windows shard 4/6) —
        // https://github.com/Kapps/weavie/actions/runs/33047702739/job/98436223300 — added this retry.
        for (let attempt = 1; attempt <= 2; attempt++) {
          if (attempt === 1) {
            await page.goto(host.url, { waitUntil: "domcontentloaded" });
          } else {
            blockedLoads.length = 0;
            await page.reload({ waitUntil: "domcontentloaded" });
          }
          // The app removes the splash element once it has booted (layout + first session). Its
          // disappearance is the "app is interactive" signal — not a fixed sleep.
          await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
          if (blockedLoads.length === 0) {
            break;
          }
          if (attempt === 2) {
            throw new Error(`the page booted without ${blockedLoads.join("; ")}`);
          }
        }
        if (dismissInferenceOffer && !automaticInference) {
          const offer = page.locator(".toast", {
            hasText: "Let Weavie use automatic inference",
          });
          await expect(offer).toBeVisible();
          await offer.getByRole("button", { name: "Dismiss" }).click();
          await expect(offer).toHaveCount(0);
        }
      } catch (error) {
        // Playwright records setup failures only after the fixture unwinds, so testInfo still says "passed" here.
        const failures = [error];
        await collectFailure(failures, dumpDiagnostics);
        await collectFailure(failures, teardown);
        throwFailures(failures, "App setup, diagnostics, or teardown failed.");
      }

      const failures: unknown[] = [];
      try {
        await use(host);
      } catch (error) {
        failures.push(error);
      }
      if (failures.length > 0 || testInfo.status !== testInfo.expectedStatus) {
        await collectFailure(failures, dumpDiagnostics);
      }
      await collectFailure(failures, teardown);
      throwFailures(failures, "Test, diagnostics, or teardown failed.");
    },
    { auto: true },
  ],
});

export { expect };
