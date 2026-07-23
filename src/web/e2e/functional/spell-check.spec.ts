import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { awaitEditorReady, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { WeavieWindow } from "../harness/weavie-window";

interface SpellFrame {
  type?: string;
  lines?: { line?: number; text?: string }[];
}

const sent: SpellFrame[] = [];
const received: SpellFrame[] = [];

function recordSpellFrame(payload: string | Buffer, target: SpellFrame[]): void {
  try {
    const frame = JSON.parse(
      typeof payload === "string" ? payload : payload.toString("utf8"),
    ) as SpellFrame;
    if (frame.type?.startsWith("spell-")) {
      target.push(frame);
    }
  } catch {
    // Non-JSON terminal frames share this socket; they are not spelling protocol traffic.
  }
}

test.use({
  preNavigate: {
    run: async (page) => {
      page.on("websocket", (socket) => {
        socket.on("framesent", (frame) => recordSpellFrame(frame.payload, sent));
        socket.on("framereceived", (frame) => recordSpellFrame(frame.payload, received));
      });
    },
  },
});

test.beforeEach(() => {
  sent.length = 0;
  received.length = 0;
});

async function wordPoint(page: Page): Promise<{ x: number; y: number }> {
  const point = await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    if (editor === undefined) {
      return null;
    }
    editor.focus();
    editor.setPosition({ lineNumber: 1, column: 2 });
    const visible = editor.getScrolledVisiblePosition({ lineNumber: 1, column: 2 });
    const rect = editor.getDomNode()?.getBoundingClientRect();
    return visible === null || rect === null
      ? null
      : { x: rect.left + visible.left + 1, y: rect.top + visible.top + visible.height / 2 };
  });
  if (point === null) {
    throw new Error("The spelling target is not visible in Monaco.");
  }
  return point;
}

test("manual spelling marks edited lines, offers corrections, and persists a project word", async ({
  page,
  weavie,
}) => {
  await openFile(page, "spell-check.txt");
  const underlines = page.locator(".monaco-editor .weavie-spell-issue");

  // The seeded typo is intentionally quiet: only a manually changed line enters spell checking.
  await expect(underlines).toHaveCount(0);

  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    editor?.focus();
    editor?.setPosition({ lineNumber: 1, column: 4 });
  });
  await page.keyboard.press("Space");

  await expect(underlines).toHaveCount(1);
  const checks = () => sent.filter((frame) => frame.type === "spell-check");
  await expect.poll(() => checks().length).toBe(1);
  expect(checks()[0]?.lines?.map((line) => line.text)).toEqual(["teh "]);
  const spellFile = join(weavie.workspace, "spell-check.txt");
  await expect.poll(async () => readFile(spellFile, "utf8")).toBe("teh \n");

  const point = await wordPoint(page);
  await page.mouse.click(point.x, point.y, { button: "right" });
  const menu = page.locator(".context-menu").first();
  await expect(menu.getByRole("button", { name: /^the$/i })).toBeVisible();
  const dictionary = menu.getByRole("button", { name: "Add to Dictionary" });
  await expect(dictionary).toHaveAttribute("aria-haspopup", "true");
  await dictionary.hover();
  const project = page.getByRole("button", { name: "Project" });
  await expect(project).toBeVisible();
  const checksBeforeDictionary = checks().length;
  await project.click();

  const dictionaryFile = join(weavie.workspace, ".weavie", "dictionary.txt");
  await expect
    .poll(async () => {
      try {
        return await readFile(dictionaryFile, "utf8");
      } catch {
        return "";
      }
    })
    .toContain("teh");

  // The dictionary update clears stale decorations, schedules the edited anchor again, and the real Core
  // response confirms that the persisted project word is now accepted.
  await expect.poll(() => checks().length).toBeGreaterThan(checksBeforeDictionary);
  expect(
    checks().every((frame) => frame.lines?.every((line) => line.text === "teh ") ?? false),
  ).toBe(true);
  await expect
    .poll(() => received.filter((frame) => frame.type === "spell-check-result").length)
    .toBeGreaterThan(checksBeforeDictionary);
  await expect(underlines).toHaveCount(0);

  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    const model = editor?.getModel();
    if (editor === undefined || model === null) {
      throw new Error("The spelling model is unavailable.");
    }
    editor.focus();
    editor.setPosition({ lineNumber: 1, column: model.getLineMaxColumn(1) });
  });
  await page.keyboard.press("Enter");
  await page.keyboard.type("persisttypo");
  await expect(underlines).toHaveCount(1);
  await expect.poll(async () => readFile(spellFile, "utf8")).toBe("teh \npersisttypo\n");

  // A fresh page owns fresh Monaco models. The startup restore reply can arrive before the controller finishes
  // constructing its editor host, so the controller must preserve it until the live SpellSession can apply it.
  const checksBeforeReload = checks().length;
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0);
  await awaitEditorReady(page);
  await openFile(page, "spell-check.txt");
  const restores = () => received.filter((frame) => frame.type === "spell-restore-result");
  await expect
    .poll(() =>
      restores().some(
        (frame) =>
          frame.lines?.some((line) => line.line === 2 && line.text === "persisttypo") ?? false,
      ),
    )
    .toBe(true);
  await expect
    .poll(() =>
      checks().some(
        (frame, index) =>
          index >= checksBeforeReload &&
          frame.lines?.some((line) => line.text === "persisttypo") === true,
      ),
    )
    .toBe(true);
  await expect(underlines).toHaveCount(1);
});
