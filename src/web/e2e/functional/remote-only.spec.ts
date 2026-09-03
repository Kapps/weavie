import { readdir } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";

// Transport/provisioning behaviors that only exist on the remote path (Weavie.Runner → worker). Tagged
// @remote so they run only under the remote project. See docs/specs/integration-testing-strategy.md.

// The runner provisions a worker and keeps its browser URL separate from its transport credential.
test("the runner hands back a clean worker URL and token @remote", async ({ weavie }) => {
  expect(new URL(weavie.url).search).toBe("");
  expect(weavie.token).toMatch(/^[0-9a-f]+$/);
});

// Default-deny transport auth: the worker rejects a wrong token and accepts the issued one.
test("the worker rejects a bad transport token and accepts the issued one @remote", async ({
  weavie,
}) => {
  const origin = new URL(weavie.url).origin;
  const bad = await fetch(`${origin}/weavie-bridge?token=deadbeefdeadbeef`);
  expect(bad.status).toBe(401);

  const good = await fetch(`${origin}/weavie-bridge?token=${weavie.token}`);
  expect(good.status).toBe(400);
});

// The WSS bridge reconnects and the app re-establishes after a reload (the remote-only buffering/auto-
// reconnect path).
test("the bridge reconnects after a reload @remote", async ({ page }) => {
  // Two full host boots in one test (the fixture's initial boot + this reload), so it needs more than the
  // default per-test budget when the box is loaded — a real hang still fails, just at the (tripled) bound.
  test.slow();
  await expect(page.locator(".layout-root")).toBeVisible();
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(page.locator(".session-inbox")).toBeHidden();
  await expect(page.locator(".layout-root")).toBeVisible();
});

// A remote worker serves a browser somewhere else, so it must not claim this machine's desktop handoff
// endpoint: doing so swallowed every "Open With" on the machine running the worker — the desktop launch
// handed its file to a window nobody was looking at, then exited without opening one.
test("the worker leaves the desktop open-with endpoint unclaimed @remote", async ({ weavie }) => {
  const entries = await readdir(join(weavie.home, ".weavie")).catch(() => [] as string[]);

  expect(entries.filter((entry) => entry.endsWith(".owner"))).toEqual([]);
});
