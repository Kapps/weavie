import { defineConfig, devices } from "@playwright/test";

// E2E tests for the web app. Three projects:
//   chromium  — the original bridge-transport specs (mock host + a real headless `ready` round-trip).
//   headless  — full-stack functional journeys against a real Weavie.Headless with a stubbed claude.
//   remote    — the transport-sensitive subset, run against Weavie.Runner (browser → WSS → worker).
// Transport is a harness parameter, not a duplicated suite: the full functional suite runs on `headless`,
// and only @cross / @remote tests also run on `remote`. See docs/specs/integration-testing-strategy.md.
// `pnpm run e2e` builds dist first; the headless/remote projects also need the C# host built.
// Every test is fully self-isolated — its own mkdtemp HOME, its own throwaway git workspace, and an
// OS-assigned port — so they run in parallel with no shared state. The per-test cost is dominated by the
// dotnet host (+ fake-claude pane) spawn; concurrency across cores is the only lever on that, so workers
// scale with the machine (a fraction of cores, leaving headroom for each test's 2-3 child dotnet processes).
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: true,
  workers: "75%",
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  reporter: "list",
  use: {
    headless: true,
    trace: "retain-on-failure",
  },
  projects: [
    {
      name: "chromium",
      testMatch: ["bridge.spec.ts", "headless-host.spec.ts", "native-bridge.spec.ts"],
      use: { ...devices["Desktop Chrome"] },
    },
    // No device preset: its canonical userAgent says "Windows", which flips Monaco/vscode to backslash
    // fs paths and breaks file opens against the Linux host. Use the browser's native (Linux) UA.
    {
      name: "headless",
      testDir: "./e2e/functional",
      grepInvert: /@remote/,
      use: { viewport: { width: 1280, height: 800 } },
    },
    {
      name: "remote",
      testDir: "./e2e/functional",
      grep: /@cross|@remote/,
      use: { viewport: { width: 1280, height: 800 } },
    },
  ],
});
