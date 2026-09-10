import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { type MessageEnvelope, parseEnvelope } from "../../src/messaging/message-envelope";
import {
  clickIntoEditor,
  openFile,
  pressDocumentEnd,
  pressDocumentStart,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";

const marks = (page: Page) => page.locator(".view-lines .weavie-misspelling");
const word = (page: Page, text: string) => marks(page).filter({ hasText: text });

function spellingRequests(page: Page): MessageEnvelope[] {
  const requests: MessageEnvelope[] = [];
  page.on("websocket", (socket) => {
    socket.on("framesent", ({ payload }) => {
      const message = typeof payload === "string" ? parseEnvelope(payload) : null;
      if (message?.kind === "request" && message.feature === "spelling") requests.push(message);
    });
  });
  return requests;
}

async function addWord(page: Page, text: string, scope: "User" | "Project"): Promise<void> {
  await word(page, text).first().click({ button: "right" });
  await page.getByRole("menuitem", { name: `Add “${text}” to Dictionary` }).hover();
  const action = page.getByRole("menuitem", { name: `${scope} Dictionary` });
  await expect(action).toHaveText(`${scope} Dictionary`);
  await action.click();
  await expect(word(page, text)).toHaveCount(0);
}

test("spell check marks identifiers, comments, strings and prose", async ({ page, weavie }) => {
  await writeFile(
    join(weavie.workspace, "hello.ts"),
    'const identifiertypoo = "stringtypoo";\n// commenttypoo\n',
  );
  await openFile(page, "hello.ts");
  await expect(marks(page)).toHaveText(["identifiertypoo", "stringtypoo", "commenttypoo"]);
  await writeFile(join(weavie.workspace, "notes.txt"), "The spelling is correct.\nprosetypoo\n");
  await openFile(page, "notes.txt");
  await expect(marks(page)).toHaveText(["prosetypoo"]);
  await clickIntoEditor(page);
  await pressDocumentStart(page);
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Shift+End");
  await page.keyboard.type("correct");
  await expect(marks(page)).toHaveCount(0);
});

test("C# declarations check class, constant, property, method, parameter and local names", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "Symbols.cs"),
    [
      "public class ClassTypoo {",
      '  public const string DiagnoasticId = "WV0001";',
      "  public int PropertyTypoo { get; set; }",
      "  public int MethodTypoo(int parameterTypoo) {",
      "    int localTypoo = 1;",
      '    string message = "stringtypoo";',
      "    // commenttypoo",
      "    return 0;",
      "  }",
      "}",
      "",
    ].join("\n"),
  );
  await openFile(page, "Symbols.cs");
  await expect(marks(page)).toHaveText([
    "Typoo",
    "Diagnoastic",
    "Typoo",
    "Typoo",
    "Typoo",
    "Typoo",
    "stringtypoo",
    "commenttypoo",
  ]);
});

test("Python declarations check unclassified variables alongside scoped names and prose", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "symbols.py"),
    [
      "class ClassTypoo:",
      "    def method_typoo(self, parameter_typoo):",
      '        local_typoo = "stringtypoo"',
      "        # commenttypoo",
      "        return len([])",
      "",
    ].join("\n"),
  );
  await openFile(page, "symbols.py");
  await expect(marks(page)).toHaveText([
    "Typoo",
    "typoo",
    "typoo",
    "typoo",
    "stringtypoo",
    "commenttypoo",
  ]);
});

test("identifier corrections preserve camel case and snake case boundaries", async ({
  page,
  weavie,
}) => {
  const file = join(weavie.workspace, "hello.ts");
  await writeFile(file, "const mispelledCount = 1;\nconst total_mispelled = 2;\n");
  await openFile(page, "hello.ts");
  await expect(marks(page)).toHaveText(["mispelled", "mispelled"]);
  await word(page, "mispelled").first().click({ button: "right" });
  await page.getByRole("menuitem", { name: /^misspelled(?:\s|$)/ }).click();
  await expect(marks(page)).toHaveText(["mispelled"]);
  await expect
    .poll(() => readFile(file, "utf8"))
    .toBe("const misspelledCount = 1;\nconst total_mispelled = 2;\n");
  // Flaked on macOS CI 2026-09-09 16:07 UTC (run 34374758357, shard 5/6): right-click landed while the
  // remaining mark's decoration element was being replaced, detaching it mid-click. Single occurrence,
  // did not reproduce on the prior run; no confirmed root cause, so no change made here.
  await word(page, "mispelled").click({ button: "right" });
  await page.getByRole("menuitem", { name: /^misspelled(?:\s|$)/ }).click();
  await expect(marks(page)).toHaveCount(0);
  await expect
    .poll(() => readFile(file, "utf8"))
    .toBe("const misspelledCount = 1;\nconst total_misspelled = 2;\n");
});

test("dictionary menu persists user and project words and observes project edits", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "notes.txt"),
    "userwordtypoo projectwordtypoo\nuserwordtypoo projectwordtypoo\nexternalwordtypoo\n",
  );
  await openFile(page, "notes.txt");
  await expect(marks(page)).toHaveCount(5);
  await addWord(page, "userwordtypoo", "User");
  await addWord(page, "projectwordtypoo", "Project");
  const userDictionary = join(weavie.home, ".weavie", "dictionary.txt");
  const projectDictionary = join(weavie.workspace, ".weavie-words");
  expect(await readFile(userDictionary, "utf8")).toContain("userwordtypoo");
  expect(await readFile(projectDictionary, "utf8")).toContain("projectwordtypoo");
  await page.reload();
  await openFile(page, "notes.txt");
  await expect(marks(page)).toHaveText(["externalwordtypoo"]);
  await writeFile(projectDictionary, "projectwordtypoo\nexternalwordtypoo\n");
  await expect(marks(page)).toHaveCount(0);
  await writeFile(userDictionary, "");
  await expect(marks(page)).toHaveText(["userwordtypoo", "userwordtypoo"]);
});

test("large code lines keep rendering while identifier grammar runs in a worker", async ({
  page,
  weavie,
}) => {
  await page.evaluate(() => {
    window.Worker = new Proxy(window.Worker, {
      construct(Target, args) {
        const worker = Reflect.construct(Target, args) as Worker;
        const frames = new Map<string, number>();
        const heartbeat = (): void => {
          for (const [request, count] of frames) frames.set(request, count + 1);
          requestAnimationFrame(heartbeat);
        };
        if (String(args[0]).includes("spell-worker-entry")) {
          requestAnimationFrame(heartbeat);
          worker.postMessage = new Proxy(worker.postMessage, {
            apply(post, owner, messages) {
              const message = messages[0] as { method: string; req: string };
              if (message.method === "$tokens") frames.set(message.req, 0);
              return Reflect.apply(post, owner, messages);
            },
          });
          worker.addEventListener("message", ({ data }) => {
            const count = frames.get(data.seq);
            if (count !== undefined) {
              document.documentElement.dataset.spellWorkerFrames = String(count);
              frames.delete(data.seq);
            }
          });
        }
        return worker;
      },
    });
  });
  await writeFile(
    join(weavie.workspace, "large.ts"),
    `const mispelledCount=0;${"const abc=0;".repeat(10_000)}\n`,
  );
  await openFile(page, "large.ts");
  await expect(word(page, "mispelled")).toBeVisible();
  expect(
    await page.evaluate(() => Number(document.documentElement.dataset.spellWorkerFrames)),
  ).toBeGreaterThan(0);
});

test("large files check only the visible viewport and refresh after scrolling", async ({
  page,
  weavie,
}) => {
  await writeFile(join(weavie.workspace, "notes.txt"), "viewporttypoo\n".repeat(20_000));
  await openFile(page, "notes.txt");
  await expect(word(page, "viewporttypoo").first()).toBeVisible();
  const decoratedLines = () =>
    page.evaluate(() => {
      const editor = window.__WEAVIE_EDITOR__;
      const model = editor?.getModel();
      return model
        ?.getAllDecorations()
        .filter((item) => item.options.description === "spelling")
        .map((item) => item.range.startLineNumber);
    });
  const firstViewport = await decoratedLines();
  expect(firstViewport?.length).toBeGreaterThan(0);
  expect(firstViewport?.length).toBeLessThan(100);
  expect(Math.max(...(firstViewport ?? []))).toBeLessThan(100);
  await clickIntoEditor(page);
  await pressDocumentEnd(page);
  await expect.poll(async () => Math.max(...((await decoratedLines()) ?? []))).toBe(20_000);
  expect((await decoratedLines())?.length).toBeLessThan(100);
});

test("Markdown checks prose without marking inline code, fences, or links", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "README.md"),
    [
      "# A short title",
      "",
      "A prosetypoo with `inlinetypoo` and https://example.com/urltypoo.",
      "",
      "```typescript",
      "const fencedtypoo = 1;",
      "```",
      "",
      "```",
      "untypedtypoo",
      "```",
      "",
      "    indentedtypoo",
      "",
      "[A label](https://example.com/linktypoo)",
      "",
    ].join("\n"),
  );
  await openFile(page, "README.md");
  await expect(marks(page)).toHaveText(["prosetypoo"]);
});

test("horizontal scrolling keeps long-line spelling requests within the viewport", async ({
  page,
  weavie,
}) => {
  const requests = spellingRequests(page);
  const checked = () =>
    requests
      .filter((message) => message.name === "check")
      .map((message) =>
        (message.payload as { spans: { text: string }[] }).spans.map((span) => span.text).join(""),
      );
  await page.reload();
  await writeFile(
    join(weavie.workspace, "notes.txt"),
    `starttypoo ${"correct ".repeat(1_000)}endtypoo\n`,
  );
  await openFile(page, "notes.txt");
  await expect(marks(page)).toHaveText(["starttypoo"]);
  expect(checked().some((text) => text.includes("starttypoo"))).toBe(true);
  expect(checked().every((text) => !text.includes("endtypoo"))).toBe(true);
  await clickIntoEditor(page);
  await page.keyboard.press("End");
  await expect(marks(page)).toHaveText(["endtypoo"]);
  expect(checked().some((text) => text.includes("endtypoo"))).toBe(true);
  expect(Math.max(...checked().map((text) => text.length))).toBeLessThan(200);
});

test("US spelling suggestions are requested on demand and corrections support undo and keyboard", async ({
  page,
  weavie,
}) => {
  const requests = spellingRequests(page);
  const suggestions = () => requests.filter((message) => message.name === "suggest");
  await page.reload();
  const file = join(weavie.workspace, "notes.txt");
  await writeFile(file, "A mispelled word.\ncolor colour\n");
  await openFile(page, "notes.txt");
  await expect(marks(page)).toHaveText(["mispelled", "colour"]);
  expect(suggestions()).toHaveLength(0);

  await word(page, "mispelled").click({ button: "right" });
  const correction = page.getByRole("menuitem", { name: /^misspelled(?:\s|$)/ });
  await expect(correction).toBeVisible();
  expect(suggestions()).toHaveLength(1);
  await correction.click();
  await expect(marks(page)).toHaveText(["colour"]);
  await expect.poll(() => readFile(file, "utf8")).toBe("A misspelled word.\ncolor colour\n");
  await page.keyboard.press("ControlOrMeta+z");
  await expect(word(page, "mispelled")).toBeVisible();
  await expect.poll(() => readFile(file, "utf8")).toBe("A mispelled word.\ncolor colour\n");
  expect(suggestions()).toHaveLength(1);

  await word(page, "mispelled").click();
  await page.keyboard.press("ControlOrMeta+Alt+s");
  await expect(correction).toBeVisible();
  expect(suggestions()).toHaveLength(2);
  await page.keyboard.press("Home");
  await expect(correction).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(marks(page)).toHaveText(["colour"]);
  await expect.poll(() => readFile(file, "utf8")).toBe("A misspelled word.\ncolor colour\n");

  await page.keyboard.press("ControlOrMeta+z");
  await expect(word(page, "mispelled")).toBeVisible();
  await word(page, "mispelled").click({ button: "right" });
  await expect(correction).toBeVisible();
  await writeFile(file, "A changed word.\ncolor colour\n");
  await expect(page.locator(".view-lines")).toContainText("A changed word.");
  await correction.click();
  await expect(page.locator(".toast", { hasText: "The spelling target changed" })).toBeVisible();
  expect(await readFile(file, "utf8")).toBe("A changed word.\ncolor colour\n");
});
