import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

const key = "editorSpellCheck.foreground";
const marks = (page: Page) => page.locator(".view-lines .weavie-misspelling");

test.use({
  colorScheme: "dark",
  fakeScript: {
    steps: [
      { op: "mcp", tool: "setSetting", args: { key: "theme.mode", value: "dark" } },
      { op: "waitFile", path: "{{WORKSPACE}}/.dark-override" },
      { op: "mcp", tool: "setThemeOverride", args: { key, value: "#ff66cc" } },
      { op: "waitFile", path: "{{WORKSPACE}}/.light-override" },
      { op: "mcp", tool: "setThemeOverride", args: { key, value: "#006b38" } },
      { op: "waitFile", path: "{{WORKSPACE}}/.remove-override" },
      { op: "mcp", tool: "removeThemeOverride", args: { key } },
    ],
  },
});

async function underlineColors(page: Page): Promise<string[]> {
  return marks(page).evaluateAll((elements) =>
    elements.map((element) => getComputedStyle(element).textDecorationColor),
  );
}

async function expectVisibleUnderlines(page: Page): Promise<string[]> {
  let colors: string[] = [];
  await expect
    .poll(async () => {
      const styles = await marks(page).evaluateAll((elements) =>
        elements.map((element) => {
          const style = getComputedStyle(element);
          return {
            color: style.textDecorationColor,
            line: style.textDecorationLine,
            thickness: style.textDecorationThickness,
            skipInk: style.textDecorationSkipInk,
          };
        }),
      );
      colors = styles.map((style) => style.color);
      return styles;
    })
    .toEqual(
      Array(3).fill({
        color: expect.stringMatching(/^rgb\(\d+, \d+, \d+\)$/),
        line: "underline",
        thickness: "2px",
        skipInk: "none",
      }),
    );
  return colors;
}

test("spelling colors remain visible across syntax and track theme overrides without stale colors", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "hello.ts"),
    'const identifiertypoo = "stringtypoo";\n// commenttypoo\n',
  );
  await openFile(page, "hello.ts");
  await expect(marks(page)).toHaveText(["identifiertypoo", "stringtypoo", "commenttypoo"]);
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "dark");
  const dark = await expectVisibleUnderlines(page);
  expect(new Set(dark).size).toBe(1);

  await writeFile(join(weavie.workspace, ".dark-override"), "");
  await expect.poll(() => underlineColors(page)).toEqual(Array(3).fill("rgb(255, 102, 204)"));
  await runCommand(page, "Cycle Theme Mode");
  await runCommand(page, "Cycle Theme Mode");
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  const light = await expectVisibleUnderlines(page);
  expect(new Set(light).size).toBe(1);
  expect(light).not.toEqual(dark);
  expect(light).not.toEqual(Array(3).fill("rgb(255, 102, 204)"));

  await writeFile(join(weavie.workspace, ".light-override"), "");
  await expect.poll(() => underlineColors(page)).toEqual(Array(3).fill("rgb(0, 107, 56)"));
  await runCommand(page, "Undo Theme Override");
  await expect.poll(() => underlineColors(page)).toEqual(light);
  await runCommand(page, "Cycle Theme Mode");
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "dark");
  await expect.poll(() => underlineColors(page)).toEqual(Array(3).fill("rgb(255, 102, 204)"));
  await writeFile(join(weavie.workspace, ".remove-override"), "");
  await expect.poll(() => underlineColors(page)).toEqual(dark);
});
