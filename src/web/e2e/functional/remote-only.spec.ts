import { expect, test } from "../harness/fixtures";

// Transport/provisioning behaviors that only exist on the remote path (Weavie.Runner → worker). Tagged
// @remote so they run only under the remote project. See docs/specs/integration-testing-strategy.md.

// The runner provisions a worker and hands the browser a tokened URL — the worker is reachable and authed.
test("the runner hands back a tokened worker URL @remote", async ({ weavie }) => {
  expect(weavie.url).toMatch(/[?&]token=[0-9a-f]+/);
});

// Default-deny auth: the worker rejects a wrong token and accepts the issued one.
test("the worker rejects a bad token and accepts the issued one @remote", async ({ weavie }) => {
  const origin = new URL(weavie.url).origin;
  const bad = await fetch(`${origin}/?token=deadbeefdeadbeef`, { redirect: "manual" });
  expect(bad.status).toBe(401);

  const good = await fetch(weavie.url, { redirect: "manual" });
  expect(good.ok).toBe(true);
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
  await expect(page.locator(".layout-root")).toBeVisible();
});
