import { expect, test } from "@playwright/test";
import { launchRunner, runnerBuilt } from "./harness/weavie-runner";

test.describe("runner browser entry", () => {
  test.skip(!runnerBuilt(), "Weavie.Runner not built (run `dotnet build src/Weavie.Runner`)");

  test("scrubs query tokens and remembers one valid token entry", async ({ page }) => {
    const runner = await launchRunner({ fakeScript: null });
    try {
      await page.goto(`${runner.url}/?token=${runner.token}`, { waitUntil: "domcontentloaded" });
      expect(new URL(page.url()).search).toBe("");

      await page.getByRole("textbox", { name: "Runner token" }).fill("wrong-token");
      await page.getByRole("button", { name: "Connect" }).click();
      await expect(page.getByRole("alert")).toHaveText("That token was not accepted.");

      await page.getByRole("textbox", { name: "Runner token" }).fill(runner.token);
      await page.getByRole("button", { name: "Connect" }).click();

      await expect(page.locator(".layout-root")).toBeVisible({ timeout: 30_000 });
      const firstWorkerUrl = new URL(page.url());
      expect(firstWorkerUrl.search).toBe("");
      expect(firstWorkerUrl.hash).toBe("");

      await page.goto(runner.url, { waitUntil: "domcontentloaded" });
      await expect(page.locator(".layout-root")).toBeVisible({ timeout: 30_000 });
      expect(new URL(page.url()).search).toBe("");
      await expect(page.getByRole("textbox", { name: "Runner token" })).toHaveCount(0);
    } finally {
      await runner.stop();
    }
  });
});
