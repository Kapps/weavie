import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { PIXEL_RED } from "../harness/git-workspace";

const mockupHtml = (heading: string): string => `<!doctype html>
<html><head>
<link rel="stylesheet" href="style.css">
<style>#inline { font-weight: 700; }</style>
</head><body>
<h1 id="heading">${heading}</h1>
<img id="pixel" src="pixel.png">
<script>
  const write = (id, text) => {
    const node = document.createElement("p");
    node.id = id;
    node.textContent = text;
    document.body.append(node);
  };
  write("inline", "built by script");
  write("origin", self.origin);
  let parentAccess = "reachable";
  try { parent.document; } catch (error) { parentAccess = error.name; }
  write("parent", parentAccess);
  let cookieAccess = "readable";
  try { document.cookie; } catch (error) { cookieAccess = error.name; }
  write("cookie", cookieAccess);
</script>
<script type="module" src="app.js"></script>
</body></html>
`;

async function seedMockup(workspace: string): Promise<void> {
  const dir = join(workspace, "mock");
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, "mockup.html"), mockupHtml("First draft"));
  await writeFile(join(dir, "style.css"), "#heading { color: rgb(200, 30, 60); }\n");
  await writeFile(join(dir, "util.js"), "export const label = 'module loaded';\n");
  await writeFile(join(dir, "data.json"), '{ "fetched": "json fetched" }\n');
  await writeFile(
    join(dir, "app.js"),
    "import { label } from './util.js';\n" +
      "const out = document.createElement('p'); out.id = 'module'; out.textContent = label;\n" +
      "document.body.append(out);\n" +
      "const data = await (await fetch('data.json')).json();\n" +
      "const fetched = document.createElement('p'); fetched.id = 'fetched'; fetched.textContent = data.fetched;\n" +
      "document.body.append(fetched);\n",
  );
  await writeFile(join(dir, "pixel.png"), PIXEL_RED);
}

const frame = (page: Page) => page.frameLocator(".editor-preview-html-frame");

test("HTML preview runs the page's scripts sandboxed and resolves its relative assets @cross", async ({
  page,
  weavie,
}) => {
  await seedMockup(weavie.workspace);
  await openFile(page, "mockup.html");
  const toggle = page.locator(".editor-preview-toggle");
  await expect(toggle).toHaveAttribute("title", /^Show preview/);
  await toggle.click();

  const doc = frame(page);
  await expect(doc.locator("#inline")).toHaveText("built by script");
  await expect(doc.locator("#inline")).toHaveCSS("font-weight", "700");
  await expect(doc.locator("#heading")).toHaveCSS("color", "rgb(200, 30, 60)");
  await expect(doc.locator("#module")).toHaveText("module loaded");
  await expect(doc.locator("#fetched")).toHaveText("json fetched");
  await expect(doc.locator("#pixel")).toHaveJSProperty("naturalWidth", 8);
  await expect(doc.locator("#origin")).toHaveText("null");
  await expect(doc.locator("#parent")).toHaveText("SecurityError");
  await expect(doc.locator("#cookie")).toHaveText("SecurityError");
});

test("HTML preview live-updates on disk writes and revokes its asset grant when closed", async ({
  page,
  weavie,
}) => {
  await seedMockup(weavie.workspace);
  await openFile(page, "mockup.html");
  const toggle = page.locator(".editor-preview-toggle");
  await toggle.click();
  await expect(frame(page).locator("#heading")).toHaveText("First draft");
  const base = await page
    .locator(".editor-preview-html-frame")
    .evaluate((element) => /<base href="([^"]+)">/.exec(element.getAttribute("srcdoc") ?? "")?.[1]);
  expect(base).toMatch(/\/weavie-preview\/[0-9a-f]{32}\/mock\/$/);
  expect((await page.request.get(`${base}style.css`)).status()).toBe(200);

  await writeFile(join(weavie.workspace, "mock", "mockup.html"), mockupHtml("Agent revision"));
  await expect(frame(page).locator("#heading")).toHaveText("Agent revision");
  await expect(frame(page).locator("#inline")).toHaveText("built by script");

  await toggle.click();
  await expect(page.locator(".editor-preview-html-frame")).toHaveCount(0);
  await expect.poll(async () => (await page.request.get(`${base}style.css`)).status()).toBe(404);
});
