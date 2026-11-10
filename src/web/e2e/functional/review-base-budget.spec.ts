import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { parseEnvelope } from "../../src/messaging/message-envelope";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { deferredPhase, describeSample, measureRender, settle } from "../harness/render-budget";
import { decodeTestWebSocketMessage } from "../harness/websocket-codec";

// Work budgets for changing an open review's base: what the page reads, fetches and builds to redraw it, counted
// rather than timed. Run: pnpm e2e:perf.
const FILES = 40;
const UNCOMMITTED = 4;
const LINES = 120;
interface Budget {
  reads: number;
  deferred: number;
  texts: number;
  editors: number;
}

// PERFORMANCE CONTRACT: raising any budget below noticeably requires the user's explicit approval — never raise one
// just to make a failure pass. Measured 2026-11-10; lower a budget whenever a change makes the switch cheaper.
// Switching a whole-branch review down to the uncommitted files (measured 67 reads, +28 deferred), then back up to
// every file (132, +61). Read budgets sit ~30% over; the counts are exact. Every file's text is fetched, on screen
// or not, so `texts` is one per file.
//
// FLAKE 2026-11-10 ~05:51 UTC, release run https://github.com/Kapps/weavie/actions/runs/42762641824 (windows, shard 4):
// +231 deferred against 190. The spell scan hit-tested every visible line (2 forced reads each) on every debounced
// rescan, so the count grew with how many scroll/layout/token events a slow runner delivered. Fixed in
// visibleSpellRanges: a line that cannot overflow the viewport is scanned without reading layout.
const TO_FEW: Budget = { reads: 95, deferred: 40, texts: UNCOMMITTED, editors: 3 };
const TO_MANY: Budget = { reads: 180, deferred: 80, texts: FILES, editors: 3 };

const names = Array.from({ length: FILES }, (_, index) => `area${index % 4}/file${index}.ts`);
// Distinct per file: review texts are content-addressed, so identical files would share one fetch.
const version = (name: string, label: string) =>
  Array.from({ length: LINES }, (_, line) =>
    line % 7 === 0 ? `// ${name} ${label} ${line}` : `export const v${line} = ${line};`,
  ).join("\n");
const git = (cwd: string, ...args: string[]): void => {
  execFileSync("git", ["-c", "user.email=e2e@example.com", "-c", "user.name=Weavie E2E", ...args], {
    cwd,
    stdio: "ignore",
  });
};
const writeAll = (workspace: string, files: string[], label: string) =>
  Promise.all(files.map((name) => writeFile(join(workspace, name), version(name, label))));

// The paths of the review texts the page asks for, recorded from the first socket on.
const texts: string[] = [];

test.describe.configure({ timeout: 180_000 });
test.use({
  trace: { mode: "retain-on-failure", snapshots: false },
  preNavigate: {
    run: async (page) => {
      await deferredPhase.run(page);
      page.on("websocket", (socket) =>
        socket.on("framesent", ({ payload }) => {
          const message = parseEnvelope(decodeTestWebSocketMessage(payload));
          if (
            message?.kind === "request" &&
            message.feature === "review" &&
            message.name === "text"
          )
            texts.push((message.payload as { path: string }).path);
        }),
      );
    },
  },
  workspaceSeed: {
    run: async (workspace) => {
      execFileSync("mkdir", ["-p", ...[0, 1, 2, 3].map((area) => join(workspace, `area${area}`))]);
      await writeAll(workspace, names, "original");
      git(workspace, "add", "-A");
      git(workspace, "commit", "-q", "-m", "base");
      git(workspace, "update-ref", "refs/remotes/origin/main", "HEAD");
      git(workspace, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
      await writeAll(workspace, names, "branch");
      git(workspace, "commit", "-q", "-am", "branch");
      await writeAll(workspace, names.slice(0, UNCOMMITTED), "uncommitted");
    },
  },
});

async function diffAgainst(page: Page, reference: string): Promise<void> {
  await runCommand(page, "Diff Against…");
  const input = page.locator(".session-prompt .session-prompt-input");
  await input.fill(reference);
  await input.press("Enter");
}

// Switches the open review to `reference`, confirming the switch, and checks what the redraw cost.
async function expectSwitchWithin(page: Page, reference: string, files: string[], budget: Budget) {
  texts.length = 0;
  const sample = await measureRender(page, 1, budget.reads, async () => {
    await diffAgainst(page, reference);
    await page.getByRole("button", { name: "Switch" }).click();
    await awaitReviewSet(
      page,
      files.map((name) => name.split("/").pop()!),
    );
    await expect(page.locator(".unified-review-file").first()).toBeVisible();
    await settle(page);
  });
  const editors = await page.evaluate(() => window.__WEAVIE_MONACO__!.editor.getEditors().length);
  const report = `switch to ${reference}: ${describeSample(sample)}\nreview texts fetched: ${texts.length}\n  ${texts.join("\n  ")}\nlive editors: ${editors}`;
  console.log(report);
  expect(sample.reads, report).toBeLessThanOrEqual(budget.reads);
  expect(sample.deferred, report).toBeLessThanOrEqual(budget.deferred);
  expect(texts.length, report).toBeLessThanOrEqual(budget.texts);
  expect(editors, report).toBeLessThanOrEqual(budget.editors);
}

test("changing the review base redraws within its budget @perf", async ({ page }) => {
  await diffAgainst(page, "origin/main");
  await awaitReviewSet(
    page,
    names.map((name) => name.split("/").pop()!),
  );
  await expect(page.locator(".unified-review-file").first()).toBeVisible();
  await settle(page);

  await expectSwitchWithin(page, "HEAD", names.slice(0, UNCOMMITTED), TO_FEW);
  await expectSwitchWithin(page, "origin/main", names, TO_MANY);
});
