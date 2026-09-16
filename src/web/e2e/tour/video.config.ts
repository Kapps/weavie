import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "@playwright/test";

// Video-enabled tour config: reuses the functional harness (real Weavie.Headless + stubbed claude) and turns
// on Playwright video for every test, so each tour spec records a .webm proving the feature. Not the committed
// suite — a throwaway capture config; project name is NOT "remote", so the harness boots the headless host.
const here = dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  testDir: here,
  // Videos land under src/web/test-results/<spec>-<title>-<project>/video.webm.
  outputDir: join(here, "..", "..", "test-results"),
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: "list",
  expect: { timeout: 15_000 },
  use: {
    headless: true,
    viewport: { width: 1280, height: 800 },
    video: "on",
    trace: "retain-on-failure",
  },
  // No device preset: its "Windows" userAgent flips Monaco/vscode to backslash paths (matches the functional
  // headless project). Named "video" so the harness treats it as headless transport, not remote.
  projects: [{ name: "video", testDir: here }],
});
