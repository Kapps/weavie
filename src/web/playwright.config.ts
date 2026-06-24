import { defineConfig, devices } from "@playwright/test";

// E2E tests for the web app. Three projects:
//   chromium  — the original bridge-transport specs (mock host + a real headless `ready` round-trip).
//   headless  — full-stack functional journeys against a real Weavie.Headless with a stubbed claude.
//   remote    — the transport-sensitive subset, run against Weavie.Runner (browser → WSS → worker).
// Transport is a harness parameter, not a duplicated suite: the full functional suite runs on `headless`,
// and only @cross / @remote tests also run on `remote`. See docs/specs/integration-testing-strategy.md.
// `pnpm run e2e` builds dist first; the headless/remote projects also need the C# host built.
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
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
      testMatch: ["bridge.spec.ts", "headless-host.spec.ts"],
      use: { ...devices["Desktop Chrome"] },
    },
    {
      name: "headless",
      testDir: "./e2e/functional",
      use: { ...devices["Desktop Chrome"] },
    },
    {
      name: "remote",
      testDir: "./e2e/functional",
      grep: /@cross|@remote/,
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
