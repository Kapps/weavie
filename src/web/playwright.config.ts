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
  // Each functional test spawns a real dotnet host (+ fake-claude + browser); the remote project adds a
  // Weavie.Runner + a worker, so a single remote test is ~3 dotnet processes. `workers` is GLOBAL across
  // projects, so it bounds how many of these heavy stacks run at once. Playwright's default heuristic is 50%
  // of cores precisely because a worker that spawns child processes needs the other cores for them: at 50%
  // on a 4-core runner, each of the 2 workers gets ~2 cores for its host/worker/browser. 75% oversubscribed
  // that — three heavy stacks fighting over four cores starved each other and the fake→hook→MCP→render
  // round-trip missed its assertion budget (the root cause behind retries). The hosted macOS/Windows runners
  // are slower and oversubscribed, so serialize there (each test gets the whole box). Trade-off: 50% on Linux
  // is ~4.3m vs ~3.5m at 75%, but it's deterministic with no retries instead of masking the contention.
  workers: process.platform === "linux" ? "50%" : 1,
  forbidOnly: Boolean(process.env.CI),
  // No retries: the flakiness was runner-resource contention (fixed by right-sizing `workers` above), not a
  // real defect, so a green run must stand on its own rather than being rescued by a re-run.
  retries: 0,
  // The `weavie` auto fixture (harness/fixtures.ts) budgets up to 40s for the host to boot and the splash
  // to clear — genuine dotnet-host + browser spawn latency, worse on the slower hosted Windows/macOS
  // runners. Playwright's 30s default per-test timeout is shorter than that budget, so on those runners
  // ANY test (not just the heavyweight PR ones already marked test.slow()) can get killed mid-boot before
  // its own body ever runs. Raise the ceiling there so the fixture's stated boot budget is actually
  // reachable; Linux keeps the default since cold boots there land in 2-6s.
  timeout: process.platform === "linux" ? 30_000 : 60_000,
  reporter: "list",
  // A weavie e2e assertion often waits on a full-stack round-trip (host + fake-claude + hook bridge + MCP +
  // render), not a DOM tick, so the 5s Playwright default is too tight even uncontended (a whole test runs
  // 2-6s cold). This ceiling is for that genuine pipeline latency, not to paper over contention — with peak
  // concurrency capped above, tests land far inside it.
  expect: { timeout: 15_000 },
  use: {
    headless: true,
    trace: "retain-on-failure",
    // Same override capture.mjs honors: run on a preinstalled Chromium (e.g. a sandbox's /opt/pw-browsers)
    // instead of the version-pinned download. Unset in normal use.
    launchOptions: { executablePath: process.env.WEAVIE_CHROMIUM || undefined },
  },
  projects: [
    {
      name: "chromium",
      testMatch: [
        "agent-markdown-links.spec.ts",
        "bridge.spec.ts",
        "headless-host.spec.ts",
        "native-bridge.spec.ts",
        "codex-composer.spec.ts",
        "process-tree.spec.ts",
      ],
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
