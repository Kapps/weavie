import type { Locator, Page } from "@playwright/test";
import { awaitEditorLaidOut, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Alt+Click on a symbol peeks its definition inline — the same embedded window Find All References uses —
// and Alt+F12 peeks at the cursor. The definition provider is mocked through __WEAVIE_MONACO__ (the harness
// bundles no language server), so these pin Weavie's gesture + command wiring and the widget opening, not
// LSP resolution. Where no provider can exist (plain text), the gesture must leave Monaco's built-in
// alt+click multicursor untouched.

import type { WeavieWindow } from "../harness/weavie-window";

async function focusEditor(page: Page, name: string): Promise<void> {
  await openFile(page, name);
  await page.locator(".monaco-editor .view-lines").first().click();
  await expect(page.locator('.editor-surface[data-kind="editor"]')).toHaveClass(/\bactive\b/);
}

// Every position resolves to hello.ts's `greet` declaration on line 1 — enough to open a real peek.
async function registerGreetDefinition(page: Page): Promise<void> {
  await page.evaluate(() => {
    const monaco = (window as WeavieWindow).__WEAVIE_MONACO__;
    if (monaco === undefined) {
      throw new Error("monaco handle not available");
    }
    monaco.languages.registerDefinitionProvider("*", {
      provideDefinition: (model) => {
        const column = model.getLineContent(1).indexOf("greet") + 1;
        return [
          {
            uri: model.uri,
            range: {
              startLineNumber: 1,
              startColumn: column,
              endLineNumber: 1,
              endColumn: column + "greet".length,
            },
          },
        ];
      },
    });
  });
}

// The rendered token for `word` on the line containing `lineText`.
//
// Monaco gives each token its own span, so the gesture can address the word itself instead of a viewport
// coordinate computed from the editor's layout. That matters: the editor's offset in the window keeps moving
// while the shell lays out and the session starts, and a coordinate measured before it settles addresses a
// place the line has left by the time the click lands — which reads as "the peek never opened" rather than
// "we clicked the wrong pixel". Waiting for the reading to stop changing wasn't enough either, because it can
// sit stably wrong for many frames while the chrome is still assembling. Handing the target to Playwright
// puts its actionability checks — visible, stable, receives pointer events — at the moment of the click.
// `last()` takes the innermost span holding the word: a highlighted line nests one span per token inside a
// span for the whole line, while plain text is a single span — this addresses the text either way, and never
// the full-width line element, whose centre can land past the end of the code.
//
// Flaked on this exact call site three times on Windows CI (issue #625 run 31993224310; 2026-08-18 runs
// 32096266021 and 32104522458 — https://github.com/Kapps/weavie/actions/runs/32104522458/job/95611908477)
// with the same fingerprint each time: `renderedLines` showed only one, empty line at teardown while
// `.editor`/`.monaco` read a healthy size — Monaco's viewport transiently clamped to 5px (see
// docs/specs/e2e-flake-analysis.md), so the word's `.view-line` never rendered and the locator waited out
// the full budget. `openFile`'s `awaitEditorLaidOut` guard only covers the layout at file-open time; any
// later relayout (a click, a `page.evaluate` mutation, even just more of the shell settling) can reopen the
// same window. Per the doc's own guidance, a third call site needing the same patch is the signal to stop
// re-applying the wait at each one and gate the shared helper instead.
//
// 2026-08-20 04:51 UTC, recurred a fourth time on this exact test — main's post-merge CI for PR #639, run
// https://github.com/Kapps/weavie/actions/runs/32333399943/job/96318875803 — despite `wordToken` already
// gating on `awaitEditorLaidOut` above. Same fingerprint: `renderedLines: [""]`, healthy `.editor`/`.monaco`
// rects at teardown, `console-errors.txt` empty. The gating check only compared Monaco's reported viewport
// height to the container's `clientHeight`; that can agree on a stale read while the DOM still holds the
// clamp's one-line placeholder, which is the actual defect signature. `awaitEditorLaidOut` (actions.ts) now
// also requires more than one `.view-line` to be rendered whenever the model has more than one line, so the
// wait matches what the doc's forensics actually showed instead of a proxy for it.
//
// Same day, that fix's own PR CI (run 32335659526) hit two fresh failures on this test and the sibling
// Alt+F12 one, both timing out at ~33.7s on the same -1 signature — the new poll had inherited the suite's
// global 30s `expect.timeout`, shorter than the ~60s budget this wait always had via the click()'s own
// actionability wait beforehand. `awaitEditorLaidOut` now sets an explicit timeout matching that budget
// instead (see its comment in actions.ts) — the check is unchanged, only its runway was too short.
async function wordToken(page: Page, lineText: string, word: string): Promise<Locator> {
  await awaitEditorLaidOut(page);
  return page
    .locator(".view-line", { hasText: lineText })
    .locator("span", { hasText: word })
    .last();
}

async function altClick(word: Locator): Promise<void> {
  await word.click({ modifiers: ["Alt"] });
}

// Flaked 2026-08-19 ~21:20 UTC, run 32302259233 (https://github.com/Kapps/weavie/actions/runs/32302259233/job/96228596384):
// 60s timeout inside word.click(), same fingerprint as the wordToken 5px-viewport-clamp flake documented
// in docs/specs/e2e-flake-analysis.md — the fourth occurrence despite that doc's existing wordToken guard.
// No fresh diagnostic data was available (a separate CI bug was silently skipping the failure-trace
// upload; fixed in e2e-platform.yml), so no test-code change is made here per that doc's "get the datum
// first" policy — see the doc for the full history and what happens on the next occurrence.
//
// Flaked again 2026-08-21, run 32439768067 (https://github.com/Kapps/weavie/actions/runs/32439768067/job/96648493335),
// same -1 (clamp-still-active) signature at the full 45s awaitEditorLaidOut budget, alongside the
// multicursor test below in the same shard. No fresh datum this time either — the shard's blob/traces
// artifacts never appear in the run, so viewport-layout.json/console-errors.txt weren't captured. See
// docs/specs/e2e-flake-analysis.md for the full history.
test("alt+click on a symbol opens the definition peek inline, and Escape closes it", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await altClick(await wordToken(page, "const message = greet", "greet"));
  const peek = page.locator(".monaco-editor .peekview-widget");
  await expect(peek).toBeVisible();
  // The peek embeds its own editor showing the definition's file — the small window into the file.
  await expect(peek.locator(".monaco-editor").first()).toBeVisible();

  await page.keyboard.press("Escape");
  await expect(peek).toHaveCount(0);
});

// Flaked 2026-08-19 ~22:01 UTC, run 32305865719 (https://github.com/Kapps/weavie/actions/runs/32305865719/job/96239656621):
// same 5px-viewport-clamp fingerprint as the sibling test above, now with confirmed forensics
// (viewport-layout.json: healthy 742x709 but renderedLines: [""]) — see docs/specs/e2e-flake-analysis.md
// for the full history and why no test-code change was made here (no repro capability, would be a guess).
test("Alt+F12 peeks the definition of the symbol at the cursor", async ({ page }) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await (await wordToken(page, "const message = greet", "greet")).click();
  await page.keyboard.press("Alt+F12");
  await expect(page.locator(".monaco-editor .peekview-widget")).toBeVisible();
});

test("alt+click without a definition provider leaves Monaco's multicursor gesture alone", async ({
  page,
}) => {
  await focusEditor(page, "notes.txt");

  await altClick(await wordToken(page, "just plain text", "plain"));
  // Monaco's default alt+click added a second cursor — the gesture declined and didn't swallow the click.
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 2,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});

// Flaked 2026-08-21, run 32439768067 (https://github.com/Kapps/weavie/actions/runs/32439768067/job/96648493335):
// same 5px-viewport-clamp fingerprint as the sibling test above, same run — see
// docs/specs/e2e-flake-analysis.md for the full history and why no test-code change was made here (no
// fresh datum, no repro capability).
test("alt+click during a multicursor session adds a cursor instead of peeking", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  // Seed a two-cursor session; alt+clicking a word must then stay Monaco's add-cursor, not a peek.
  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    if (editor === undefined) {
      throw new Error("editor handle not available");
    }
    editor.setSelections([
      {
        selectionStartLineNumber: 1,
        selectionStartColumn: 1,
        positionLineNumber: 1,
        positionColumn: 1,
      },
      {
        selectionStartLineNumber: 2,
        selectionStartColumn: 3,
        positionLineNumber: 2,
        positionColumn: 3,
      },
    ]);
  });
  // `wordToken` re-waits for editor layout itself (see its doc comment) — no separate guard needed here.
  await altClick(await wordToken(page, "const message = greet", "greet"));
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 3,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});
