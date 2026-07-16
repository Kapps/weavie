import { openFile } from "../harness/actions";
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

// Real browser -> WSS -> HostCore coverage. Codex resolves to the deterministic unavailable structured session,
// preserving its provider identity and routing without launching a real model.
test("Claude and Codex sessions restore their own tabs and active image within one second", async ({
  page,
}) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);

  await page.locator(".session-rail-add").click();
  const prompt = page.locator(".session-prompt");
  await expect(prompt).toBeVisible();
  await prompt.locator(".session-prompt-select").nth(1).selectOption("codex");
  await prompt.getByRole("combobox", { name: "Branch name" }).fill("codex-switch");
  await prompt.locator(".session-prompt-btn-primary").click();

  await expect(chips).toHaveCount(2);
  await expect(page.locator('.session-chip.active[title^="codex-switch —"]')).toBeVisible();
  await expect(
    page.locator('[data-kind="terminal:claude"][data-surface="structured-agent"]'),
  ).toBeVisible();
  await expect(page.locator(".agent-surface .pane-label")).toHaveText("Codex");

  await openFile(page, "README.md");
  await openFile(page, "pixel.png");
  await expectTabs(page, ["README.md", "pixel.png"], "pixel.png");
  const image = page.locator(".editor-media img");
  await expect(image).toHaveJSProperty("naturalWidth", 8);
  const codexMedia = new URL((await image.getAttribute("src")) as string);
  expect(codexMedia.searchParams.get("session")).toBeTruthy();
  expect(codexMedia.searchParams.get("path")).toMatch(/[\\/]pixel\.png$/);

  // The click path flushes the outgoing tab set before HostCore switches, so no debounce or sleep is needed.
  await page.locator('.session-chip[title^="main —"]').click();
  await expect(page.locator('.session-chip.active[title^="main —"]')).toBeVisible();
  await openFile(page, "hello.ts");
  await openFile(page, "notes.txt");
  await page.locator(".editor-tab", { hasText: "hello.ts" }).click();
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]hello\.ts$/);
  await expectTabs(page, ["hello.ts", "notes.txt"], "hello.ts");

  const claudeToCodex = await measureSessionSwitch(page, {
    label: "codex-switch",
    provider: "codex",
    tabs: ["README.md", "pixel.png"],
    activeTab: "pixel.png",
    content: {
      kind: "image",
      pathSuffix: "/pixel.png",
      sessionId: codexMedia.searchParams.get("session") as string,
    },
  });
  await expect(page.locator('.session-chip.active[title^="codex-switch —"]')).toBeVisible();
  await expect(
    page.locator('[data-kind="terminal:claude"][data-surface="structured-agent"]'),
  ).toBeVisible();
  await expectTabs(page, ["README.md", "pixel.png"], "pixel.png");
  await expect(image).toHaveJSProperty("naturalWidth", 8);
  expect(new URL((await image.getAttribute("src")) as string).searchParams.get("session")).toBe(
    codexMedia.searchParams.get("session"),
  );

  const codexToClaude = await measureSessionSwitch(page, {
    label: "main",
    provider: "claude",
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

  const measurements = { budgetMs: SWITCH_BUDGET_MS, claudeToCodex, codexToClaude };
  await test.info().attach("full-stack-session-switch-performance.json", {
    body: Buffer.from(JSON.stringify(measurements, null, 2)),
    contentType: "application/json",
  });
  expect(
    claudeToCodex,
    `full-stack Claude -> Codex switch exceeded ${SWITCH_BUDGET_MS}ms`,
  ).toBeLessThan(SWITCH_BUDGET_MS);
  expect(
    codexToClaude,
    `full-stack Codex -> Claude switch exceeded ${SWITCH_BUDGET_MS}ms`,
  ).toBeLessThan(SWITCH_BUDGET_MS);
});
