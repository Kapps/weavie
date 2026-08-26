import type { Page } from "@playwright/test";
import { clickIntoEditor, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { WeavieWindow } from "../harness/weavie-window";

// Regression (feat/find-references): the editor right-click menu had lost its code-intelligence actions,
// leaving only Cut / Copy / Paste / Command Palette. The menu carries the code-intelligence commands ABOVE
// the clipboard group — Go to Definition (F12), Peek Definition (Alt+F12), Find All References (Shift+F12),
// Rename Symbol (F2) — each advertising its shortcut (read live from the command catalog via formatKey) and
// each discoverable in the palette. These pin the menu order, the shortcut labels, and palette
// discoverability. The underlying Monaco/LSP action is intentionally NOT exercised: no language server is
// bundled in the harness, and the regression being guarded is the missing menu affordance + command wiring,
// not LSP navigation itself (peek behavior lives in editor-peek-definition.spec.ts).

async function focusEditor(page: Page): Promise<void> {
  await openFile(page, "hello.ts");
  // Click into Monaco so the editor pane is the focused pane (editorFocused = true) — the gate the three
  // commands carry, and the pre-open focus the palette evaluates `when` against.
  await clickIntoEditor(page);
  await expect(page.locator('.editor-surface[data-kind="editor"]')).toHaveClass(/\bactive\b/);
}

test("editor right-click menu lists the code-intelligence actions above the clipboard group, each with its shortcut", async ({
  page,
}) => {
  await focusEditor(page);

  // Right-click inside Monaco → Weavie's OWN context menu (Monaco's native menu is disabled via contextmenu:false).
  await page.locator(".monaco-editor .view-line").first().click({ button: "right" });
  const menu = page.locator(".context-menu");
  await expect(menu).toBeVisible();

  // The full ordered structure: each row as its visible label, each separator as a sentinel — so the exact
  // grouping (three intel rows, sep, clipboard trio, sep, palette) is pinned, not just membership.
  const structure = await menu.evaluate((el) =>
    [...el.children]
      .map((c) =>
        c.classList.contains("context-menu-sep")
          ? "—separator—"
          : c.classList.contains("context-menu-item")
            ? (c.querySelector("span")?.textContent ?? "")
            : null,
      )
      .filter((x) => x !== null),
  );
  expect(structure).toEqual([
    "Go to Definition",
    "Peek Definition",
    "Find All References",
    "Rename Symbol",
    "—separator—",
    "Revise Selection",
    "—separator—",
    "Cut",
    "Copy",
    "Paste",
    "—separator—",
    "Command Palette",
  ]);

  // Each new row advertises its keybinding on the right, formatted from the catalog (formatKey).
  const keysOf = (label: string) =>
    menu.locator(".context-menu-item", { hasText: label }).locator(".context-menu-keys");
  await expect(keysOf("Go to Definition")).toHaveText("F12");
  await expect(keysOf("Peek Definition")).toHaveText("Alt+F12");
  await expect(keysOf("Find All References")).toHaveText("Shift+F12");
  await expect(keysOf("Rename Symbol")).toHaveText("F2");
});

test("the code-intelligence commands are discoverable in the palette with their category and shortcut", async ({
  page,
}) => {
  await focusEditor(page);
  const box = page.locator(".tb-omnibar-box");
  const input = page.locator(".tb-omnibar-input");

  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+p");
    await expect(box).toHaveClass(/\bopen\b/, { timeout: 1000 });
  }).toPass({ timeout: 10_000 });

  const expectCommand = async (
    query: string,
    title: string,
    category: string,
    keys: string,
  ): Promise<void> => {
    await input.fill(query);
    const row = page.locator(".tb-omnibar-row", { hasText: title });
    await expect(row.first()).toBeVisible();
    await expect(row.locator(".tb-row-dir").first()).toHaveText(category);
    await expect(row.locator(".tb-row-keys").first()).toHaveText(keys);
  };

  await expectCommand(">definition", "Go to Definition", "Navigation", "F12");
  await expectCommand(">peek", "Peek Definition", "Navigation", "Alt+F12");
  await expectCommand(">references", "Find All References", "Navigation", "Shift+F12");
  await expectCommand(">rename", "Rename Symbol", "Editor", "F2");
});

test("go to definition marks its preview tab as the omnibar's current file", async ({ page }) => {
  await focusEditor(page);
  await page.evaluate(() => {
    const monaco = (window as WeavieWindow).__WEAVIE_MONACO__;
    if (monaco === undefined) {
      throw new Error("monaco handle not available");
    }
    monaco.languages.registerDefinitionProvider("*", {
      provideDefinition: (model) => {
        const prefix = "weavie-session:";
        const owner = JSON.parse(decodeURIComponent(model.uri.fragment.slice(prefix.length))) as {
          hostPath: string;
        };
        owner.hostPath = owner.hostPath.replace(/hello\.ts$/, "notes.txt");
        return [
          {
            uri: model.uri.with({
              path: model.uri.path.replace(/hello\.ts$/, "notes.txt"),
              fragment: `${prefix}${encodeURIComponent(JSON.stringify(owner))}`,
            }),
            range: {
              startLineNumber: 1,
              startColumn: 1,
              endLineNumber: 1,
              endColumn: 5,
            },
          },
        ];
      },
    });
  });

  await page.locator(".view-line", { hasText: "const message = greet" }).click();
  await page.keyboard.press("F12");

  const preview = page.locator(".editor-tab.preview.active", { hasText: "notes.txt" });
  await expect(preview).toBeVisible();
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /notes\.txt$/);

  const input = page.locator(".tb-omnibar-input");
  await input.click();
  await input.fill("notes");
  await expect(page.locator(".tb-omnibar-row.current", { hasText: "notes.txt" })).toBeVisible();
});
