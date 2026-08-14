import { createSession, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { measureSessionSwitch } from "../harness/session-switch";

const SWITCH_BUDGET_MS = 1_000;

const tabLabels = (page: import("@playwright/test").Page) =>
  page.locator(".editor-tab .editor-tab-label");

async function expectTabs(
  page: import("@playwright/test").Page,
  labels: string[],
  active: string,
): Promise<void> {
  await expect(tabLabels(page)).toHaveText(labels);
  await expect(page.locator(".editor-tab.active .editor-tab-label")).toHaveText(active);
}

// Real browser -> WSS -> HostCore coverage. Fake ACP runs through the production generic ACP process boundary.
test("Claude and ACP sessions restore their own tabs and active image within one second", async ({
  page,
}) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);

  await createSession(page, { branch: "acp-switch", provider: "fake-acp" });

  await expect(chips).toHaveCount(2);
  await expect(page.locator('.session-chip.active[title^="acp-switch —"]')).toBeVisible();
  await expect(
    page.locator('[data-kind="terminal:claude"][data-surface="structured-agent"]'),
  ).toBeVisible();
  await expect(page.locator(".agent-surface .pane-label")).toHaveText("Fake ACP");

  await openFile(page, "README.md");
  await openFile(page, "pixel.png");
  await expectTabs(page, ["README.md", "pixel.png"], "pixel.png");
  const image = page.locator(".editor-media img");
  await expect(image).toHaveJSProperty("naturalWidth", 8);
  const acpMedia = new URL((await image.getAttribute("src")) as string);
  expect(acpMedia.searchParams.get("session")).toBeTruthy();
  expect(acpMedia.searchParams.get("path")).toMatch(/[\\/]pixel\.png$/);

  // Selection rebinds the shared surfaces to the target's already-owned state. Wait for that surface before
  // driving session-scoped UI such as the omnibar.
  await page.locator('.session-chip[title^="main —"]').click();
  await expect(page.locator('.session-chip.active[title^="main —"]')).toBeVisible();
  await expect(
    page.locator('[data-kind="terminal:claude"][data-surface="terminal"]'),
  ).toBeVisible();
  await openFile(page, "hello.ts");
  await openFile(page, "notes.txt");
  await page.locator(".editor-tab", { hasText: "hello.ts" }).click();
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]hello\.ts$/);
  await expectTabs(page, ["hello.ts", "notes.txt"], "hello.ts");

  const claudeToAcp = await measureSessionSwitch(page, {
    label: "acp-switch",
    surface: "structured-agent",
    tabs: ["README.md", "pixel.png"],
    activeTab: "pixel.png",
    content: {
      kind: "image",
      pathSuffix: "/pixel.png",
      sessionId: acpMedia.searchParams.get("session") as string,
    },
  });
  await expect(page.locator('.session-chip.active[title^="acp-switch —"]')).toBeVisible();
  await expect(
    page.locator('[data-kind="terminal:claude"][data-surface="structured-agent"]'),
  ).toBeVisible();
  await expectTabs(page, ["README.md", "pixel.png"], "pixel.png");
  await expect(image).toHaveJSProperty("naturalWidth", 8);
  expect(new URL((await image.getAttribute("src")) as string).searchParams.get("session")).toBe(
    acpMedia.searchParams.get("session"),
  );

  const acpToClaude = await measureSessionSwitch(page, {
    label: "main",
    surface: "terminal",
    tabs: ["hello.ts", "notes.txt"],
    activeTab: "hello.ts",
    content: { kind: "text", pathSuffix: "/hello.ts", marker: "greet" },
  });
  await expect(page.locator('.session-chip.active[title^="main —"]')).toBeVisible();
  await expect(
    page.locator('[data-kind="terminal:claude"][data-surface="terminal"]'),
  ).toBeVisible();
  await expect(page.locator(".terminal-surface .pane-label").first()).toHaveText("Claude Code");
  await expectTabs(page, ["hello.ts", "notes.txt"], "hello.ts");
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]hello\.ts$/);
  await expect(page.locator(".monaco-editor .view-lines").first()).toContainText("greet");
  await expect(page.locator(".editor-media")).toHaveCount(0);

  const measurements = { budgetMs: SWITCH_BUDGET_MS, claudeToAcp, acpToClaude };
  await test.info().attach("full-stack-session-switch-performance.json", {
    body: Buffer.from(JSON.stringify(measurements, null, 2)),
    contentType: "application/json",
  });
  expect(
    claudeToAcp,
    `full-stack Claude -> ACP switch exceeded ${SWITCH_BUDGET_MS}ms`,
  ).toBeLessThan(SWITCH_BUDGET_MS);
  expect(
    acpToClaude,
    `full-stack ACP -> Claude switch exceeded ${SWITCH_BUDGET_MS}ms`,
  ).toBeLessThan(SWITCH_BUDGET_MS);
});
