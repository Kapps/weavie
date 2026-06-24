import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The change-review seam, driven by the fake claude's IDE-MCP openDiff. The hook gate + diff presentation
// are loopback inside the worker on both transports, so the review UI is identical; tagged @cross to also
// exercise it over the remote bridge.

const sleep = { op: "sleep" as const, ms: 1500 };
function openDiff(contents: string) {
  return {
    op: "mcp" as const,
    server: "ide" as const,
    tool: "openDiff",
    args: {
      old_file_path: "{{WORKSPACE}}/hello.ts",
      new_file_path: "{{WORKSPACE}}/hello.ts",
      new_file_contents: contents,
      tab_name: "hello.ts",
    },
  };
}

test.describe("openDiff review", () => {
  test.use({
    fakeScript: { steps: [sleep, openDiff("// DIFF_MARKER kept\nexport const answer = 42;\n")] },
  });

  test("keeping a proposed edit applies the change @cross", async ({ page }) => {
    const keep = page.locator(".weavie-inline-accept");
    await expect(keep).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

    await keep.click();

    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    await expect(page.locator(".monaco-editor .view-lines")).toContainText("DIFF_MARKER");
  });
});

test.describe("change navigation", () => {
  // Two separated edits → two hunks, so the review walk has something to navigate.
  const twoHunks =
    "export function greet(name: string): string {\n" +
    "  return `Hi there, ${name}!`;\n" +
    "}\n\n" +
    'const message = greet("weavie");\n' +
    "console.error(message);\n";
  test.use({ fakeScript: { steps: [sleep, openDiff(twoHunks)] } });

  test("the next-change control moves through the diff's hunks @cross", async ({ page }) => {
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });
    // The editor caret jumps to each change as you navigate; its vertical position is the observable.
    const caretTop = () =>
      page
        .locator(".monaco-editor .cursors-layer .cursor")
        .first()
        .evaluate((el) => Number.parseFloat((el as HTMLElement).style.top) || 0);

    const next = page.locator(".weavie-inline-nav").nth(1); // ↓ next change
    await next.click();
    const firstChange = await caretTop();
    expect(firstChange).toBeGreaterThan(0);

    await next.click();
    await expect.poll(caretTop).toBeGreaterThan(firstChange); // advanced to the second hunk
  });
});

test.describe("per-session diff state", () => {
  test.use({ fakeScript: { steps: [sleep, openDiff("// SESSION_A_DIFF\n")] } });

  // "Diff navigation between sessions": diffs are per-session state, so switching away and back to a session
  // restores its review — not a global walk across sessions.
  test("a session keeps its diff across a switch @cross", async ({ page }) => {
    const chips = page.locator(".session-chip");
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });

    await runCommand(page, "Fork Session");
    await expect(chips).toHaveCount(2);

    // Back to the first session — its diff review is still there.
    await chips.first().click();
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible();
    await expect(page.locator(".monaco-editor .view-lines")).toContainText("SESSION_A_DIFF");
  });
});
