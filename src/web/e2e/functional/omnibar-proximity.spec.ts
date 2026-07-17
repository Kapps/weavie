import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Pins the omnibar's currentFile→proximity wiring: among equal fuzzy matches, rankFiles breaks ties by
// tree distance to the ACTIVE file's directory (ranking math unit-covered in file-search.test.ts). This
// proves Omnibar really feeds it the active file + workspace root — the winner flips with the active file.
test("equal fuzzy matches rank by proximity to the active file", async ({ weavie, page }) => {
  const ws = weavie.workspace;
  for (const rel of [
    "config.ts",
    "packages/app/index.ts",
    "packages/app/config.ts",
    "packages/lib/util.ts",
    "packages/lib/config.ts",
  ]) {
    mkdirSync(join(ws, dirname(rel)), { recursive: true });
    writeFileSync(join(ws, rel), `export const x = ${JSON.stringify(rel)};\n`);
  }

  const input = page.locator(".tb-omnibar-input");
  const firstDir = page.locator(".tb-omnibar-list .tb-omnibar-row").first().locator(".tb-row-dir");

  await openFile(page, "index.ts");
  await input.click();
  await input.fill("config");
  await expect(firstDir).toHaveText("packages/app");

  await openFile(page, "util.ts");
  await input.click();
  await input.fill("config");
  await expect(firstDir).toHaveText("packages/lib");
});
