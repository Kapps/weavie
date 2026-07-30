import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page, WebSocketRoute } from "@playwright/test";
import { awaitEditorReady, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { WeavieWindow } from "../harness/weavie-window";

interface SpellFrame {
  type?: string;
  path?: string;
  content?: string;
  documentRevision?: number;
  issues?: { line?: number; word?: string }[];
}

const sent: SpellFrame[] = [];
const received: SpellFrame[] = [];
let holdNextSuggestion = false;
let heldSuggestion: {
  route: WebSocketRoute;
  payload: string | Buffer;
} | null = null;
let suggestionBarrierSequence = 0;

interface SuggestionProcessingBarrier {
  route: WebSocketRoute;
  key: string;
  message: string;
}

function parseSpellFrame(payload: string | Buffer): SpellFrame | null {
  try {
    return JSON.parse(
      typeof payload === "string" ? payload : payload.toString("utf8"),
    ) as SpellFrame;
  } catch {
    // Non-JSON terminal frames share this socket; they are not spelling protocol traffic.
    return null;
  }
}

function recordSpellFrame(payload: string | Buffer, target: SpellFrame[]): void {
  const frame = parseSpellFrame(payload);
  if (frame?.type?.startsWith("spell-")) {
    target.push(frame);
  }
}

test.use({
  preNavigate: {
    run: async (page) => {
      await page.routeWebSocket(/\/weavie-bridge(?:\?|$)/, (route) => {
        const server = route.connectToServer();
        server.onMessage((payload) => {
          if (holdNextSuggestion && parseSpellFrame(payload)?.type === "spell-suggest-result") {
            holdNextSuggestion = false;
            heldSuggestion = { route, payload };
            return;
          }
          route.send(payload);
        });
      });
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
  holdNextSuggestion = false;
  heldSuggestion = null;
  suggestionBarrierSequence = 0;
});

function releaseSuggestion(): SuggestionProcessingBarrier {
  const held = heldSuggestion;
  if (held === null) {
    throw new Error("No spelling suggestion result is being held.");
  }
  heldSuggestion = null;
  const sequence = ++suggestionBarrierSequence;
  const key = `e2e-spell-processing-${sequence}`;
  const message = `Spell response processed ${sequence}`;
  held.route.send(held.payload);
  held.route.send(JSON.stringify({ type: "notify", level: "info", message, key }));
  return { route: held.route, key, message };
}

async function waitForSuggestionProcessing(
  page: Page,
  barrier: SuggestionProcessingBarrier,
): Promise<void> {
  const toast = page.getByRole("alert").filter({ hasText: barrier.message });
  await expect(toast).toBeVisible();
  barrier.route.send(JSON.stringify({ type: "notify-clear", key: barrier.key }));
  await expect(toast).toHaveCount(0);
}

async function wordPoint(
  page: Page,
  lineNumber: number,
  column: number,
): Promise<{ x: number; y: number }> {
  const point = await page.evaluate(
    ({ lineNumber, column }) => {
      const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
      if (editor === undefined) {
        return null;
      }
      editor.focus();
      editor.setPosition({ lineNumber, column });
      const visible = editor.getScrolledVisiblePosition({ lineNumber, column });
      const rect = editor.getDomNode()?.getBoundingClientRect();
      return visible === null || rect === null
        ? null
        : { x: rect.left + visible.left + 1, y: rect.top + visible.top + visible.height / 2 };
    },
    { lineNumber, column },
  );
  if (point === null) {
    throw new Error("The spelling target is not visible in Monaco.");
  }
  return point;
}

test("spelling checks an open document, offers corrections, and survives reload", async ({
  page,
  weavie,
}) => {
  await openFile(page, "spell-check.txt");
  const underlines = page.locator(".monaco-editor .weavie-spell-issue");
  await expect(underlines).toHaveCount(1);
  const updates = () => sent.filter((frame) => frame.type === "spell-document-changed");
  const diagnostics = () => received.filter((frame) => frame.type === "spell-diagnostics");
  await expect.poll(() => updates().length).toBe(1);
  expect(updates()[0]?.content).toBe("teh\n");
  expect(updates()[0]?.documentRevision).toBe(1);
  const spellFile = join(weavie.workspace, "spell-check.txt");

  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    const model = editor?.getModel();
    if (editor === undefined || model === null) {
      throw new Error("The spelling model is unavailable.");
    }
    editor.focus();
    editor.setPosition({ lineNumber: 1, column: model.getLineMaxColumn(1) });
  });
  holdNextSuggestion = true;
  await page.keyboard.press("F7");
  const menu = page.locator(".context-menu").first();
  await expect.poll(() => heldSuggestion !== null).toBe(true);
  await expect(menu).toHaveAttribute("role", "status");
  await expect(menu).toHaveAttribute("aria-busy", "true");
  await expect(menu).toHaveAttribute("aria-live", "polite");
  await expect(menu).toContainText("Checking spelling…");
  await expect(menu).toBeFocused();
  await expect(menu.getByRole("button")).toHaveCount(0);
  const cursorBeforePendingKey = await page.evaluate(
    () => (window as WeavieWindow).__WEAVIE_EDITOR__?.getPosition() ?? null,
  );
  await page.keyboard.press("ArrowDown");
  await expect(menu).toBeFocused();
  expect(
    await page.evaluate(() => (window as WeavieWindow).__WEAVIE_EDITOR__?.getPosition() ?? null),
  ).toEqual(cursorBeforePendingKey);
  await page.keyboard.press("Escape");
  await expect(page.locator(".context-menu")).toHaveCount(0);
  await waitForSuggestionProcessing(page, releaseSuggestion());
  await expect(page.locator(".context-menu")).toHaveCount(0);

  await page.evaluate(() => (window as WeavieWindow).__WEAVIE_EDITOR__?.focus());
  holdNextSuggestion = true;
  await page.keyboard.press("F7");
  await expect.poll(() => heldSuggestion !== null).toBe(true);
  await expect(menu).toHaveAttribute("aria-busy", "true");
  await expect(menu.getByRole("button")).toHaveCount(0);
  await waitForSuggestionProcessing(page, releaseSuggestion());
  await expect(menu).toHaveAttribute("role", "menu");
  const correction = menu.getByRole("button", { name: /^the$/i });
  await expect(correction).toBeFocused();
  const dictionary = menu.getByRole("button", { name: "Add to Dictionary" });
  await expect(correction).toBeVisible();
  await expect(dictionary).toBeVisible();
  await correction.click();
  await expect.poll(async () => readFile(spellFile, "utf8")).toBe("the\n");
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
  await page.keyboard.type("projectwurd");
  await expect(underlines).toHaveCount(1);

  const point = await wordPoint(page, 2, 2);
  await page.mouse.click(point.x, point.y, { button: "right" });
  const dictionaryMenu = page.locator(".context-menu").first();
  const addToDictionary = dictionaryMenu.getByRole("button", { name: "Add to Dictionary" });
  await expect(addToDictionary).toBeVisible();
  await expect(
    dictionaryMenu.getByRole("button", { name: "projector", exact: true }),
  ).toBeVisible();
  await addToDictionary.hover();
  const project = page.getByRole("button", { name: /^Project(?:\s|$)/ });
  await expect(addToDictionary).toHaveAttribute("aria-expanded", "true");
  await expect(project).toBeVisible();
  const updatesBeforeDictionary = updates().length;
  const diagnosticsBeforeDictionary = diagnostics().length;
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
    .toContain("projectwurd");

  // The projected dictionary notification invalidates diagnostics and resubmits every open document.
  await expect.poll(() => updates().length).toBeGreaterThan(updatesBeforeDictionary);
  await expect.poll(() => diagnostics().length).toBeGreaterThan(diagnosticsBeforeDictionary);
  await expect(underlines).toHaveCount(0);

  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    const model = editor?.getModel();
    if (editor === undefined || model === null) {
      throw new Error("The spelling model is unavailable.");
    }
    editor.focus();
    editor.setPosition({ lineNumber: 2, column: model.getLineMaxColumn(2) });
  });
  await page.keyboard.press("Enter");
  await page.keyboard.type("persisttypo");
  await expect(underlines).toHaveCount(1);
  await expect
    .poll(async () => readFile(spellFile, "utf8"))
    .toBe("the\nprojectwurd\npersisttypo\n");

  // A rebuilt editor submits each restored working copy like any newly opened document.
  const updatesBeforeReload = updates().length;
  const diagnosticsBeforeReload = diagnostics().length;
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0);
  await awaitEditorReady(page);
  await openFile(page, "spell-check.txt");
  await expect.poll(() => updates().length).toBeGreaterThan(updatesBeforeReload);
  await expect
    .poll(() =>
      diagnostics()
        .slice(diagnosticsBeforeReload)
        .some(
          (frame) =>
            frame.documentRevision !== undefined &&
            (frame.issues?.some((issue) => issue.line === 3 && issue.word === "persisttypo") ??
              false),
        ),
    )
    .toBe(true);
  await expect(underlines).toHaveCount(1);
});
