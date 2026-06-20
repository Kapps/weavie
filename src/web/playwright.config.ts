import { defineConfig, devices } from "@playwright/test";

// End-to-end tests for the web app's remote bridge transport. They serve the *built* app (dist/) from
// an in-process mock host and drive it in headless Chromium, so the WebSocket bridge path is exercised
// exactly as remote/web Weavie will use it. The mock host is started inside each test (no external
// webServer); `pnpm run e2e` builds dist first. See e2e/mock-host.ts.
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
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
