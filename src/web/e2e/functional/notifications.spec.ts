import { writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Session attention (docs/specs/session-attention.md): a turn completing / a permission prompt pushes
// session-attention through the full stack (fake claude → hooks → HostCore → WSS → web), and the web plays
// a pack sound + raises a silent OS notification naming the session, whose click focuses that session.
// The presentation sinks (Notification, HTMLMediaElement.play) are stubbed and recorded before boot (the
// preNavigate fixture option) — the assertions are on the decisions, never on real audio or OS UI.

const SIGNAL = ".weavie-e2e-attention-signal";

// What the recorder stubs capture, exposed on window for the test to read back.
interface RecorderWindow {
  __notifications: { title: string; body: string; tag: string; silent: boolean }[];
  __notificationClicks: (() => void)[];
  __sounds: string[];
  __permissionRequests: number;
}

// A preNavigate option installing the recorders (a fake Notification class, a play() recorder, a pinned
// document.hasFocus) before the app boots.
function recorders(opts: { focused: boolean; permission?: "granted" | "default" | "denied" }): {
  run: (page: Page) => Promise<void>;
} {
  return {
    run: (page) =>
      page.addInitScript(
        ({ focused, permission }) => {
          const w = window as unknown as RecorderWindow & { Notification: unknown };
          w.__notifications = [];
          w.__notificationClicks = [];
          w.__sounds = [];
          w.__permissionRequests = 0;
          class RecordedNotification {
            static permission = permission;
            static requestPermission(): Promise<string> {
              w.__permissionRequests += 1;
              return Promise.resolve("granted");
            }
            onclick: (() => void) | null = null;
            constructor(
              title: string,
              options?: { body?: string; tag?: string; silent?: boolean },
            ) {
              w.__notifications.push({
                title,
                body: options?.body ?? "",
                tag: options?.tag ?? "",
                silent: options?.silent ?? false,
              });
              w.__notificationClicks.push(() => this.onclick?.());
            }
            close(): void {}
          }
          w.Notification = RecordedNotification;
          document.hasFocus = () => focused;
          HTMLMediaElement.prototype.play = function (this: HTMLMediaElement) {
            w.__sounds.push(this.src);
            return Promise.resolve();
          };
        },
        { focused: opts.focused, permission: opts.permission ?? "granted" },
      ),
  };
}

const notifications = (page: Page): Promise<RecorderWindow["__notifications"]> =>
  page.evaluate(() => (window as unknown as RecorderWindow).__notifications);
const sounds = (page: Page): Promise<string[]> =>
  page.evaluate(() => (window as unknown as RecorderWindow).__sounds);

// SessionStart → UserPromptSubmit leaves the session Working; waitFile holds the turn open until the test
// signals; the closing hook is the attention transition under test. {{WORKSPACE}} resolves in the fake to
// ITS OWN cwd, so in a two-session test each session is released independently.
const turnScript = (closing: Record<string, unknown>) => ({
  steps: [
    { op: "hook" as const, request: { hook_event_name: "SessionStart", source: "startup" } },
    { op: "hook" as const, request: { hook_event_name: "UserPromptSubmit" } },
    { op: "waitFile" as const, path: `{{WORKSPACE}}/${SIGNAL}` },
    { op: "hook" as const, request: closing },
  ],
});

test.describe("turn complete, window unfocused", () => {
  test.use({
    fakeScript: turnScript({ hook_event_name: "Stop" }),
    preNavigate: recorders({ focused: false }),
  });

  test("plays the pack sound + raises a silent notification naming the session + badges the title", async ({
    page,
    weavie,
  }) => {
    writeFileSync(join(weavie.workspace, SIGNAL), "");

    await expect.poll(() => notifications(page)).toHaveLength(1);
    const [notification] = await notifications(page);
    expect(notification.body).toBe("Turn complete — waiting on you.");
    expect(notification.title.length).toBeGreaterThan(0); // names the session (its rail label)
    expect(notification.tag).toMatch(/^local:/); // per-session tag → repeat pings coalesce
    expect(notification.silent).toBe(true); // the pack player owns audio, never the OS
    await expect.poll(() => sounds(page)).toHaveLength(1);
    expect((await sounds(page))[0]).toContain("/sounds/weavie/sounds/turn-complete.wav");
    // The tab title carries the ● badge until the window regains focus.
    expect(await page.title()).toMatch(/^●/);
  });
});

test.describe("permission prompt, window unfocused", () => {
  test.use({
    fakeScript: turnScript({
      hook_event_name: "Notification",
      message: "Claude needs your permission to use Bash",
    }),
    preNavigate: recorders({ focused: false }),
  });

  test("raises a needs-input notification with its own sound", async ({ page, weavie }) => {
    writeFileSync(join(weavie.workspace, SIGNAL), "");

    await expect.poll(() => notifications(page)).toHaveLength(1);
    expect((await notifications(page))[0].body).toBe("Needs your input.");
    await expect.poll(() => sounds(page)).toHaveLength(1);
    expect((await sounds(page))[0]).toContain("/sounds/weavie/sounds/needs-input.wav");
  });
});

test.describe("two sessions: the background session pings; clicking focuses it", () => {
  test.use({
    fakeScript: turnScript({ hook_event_name: "Stop" }),
    preNavigate: recorders({ focused: false }),
  });

  test("notification names the background session and its click switches the rail to it", async ({
    page,
    weavie,
  }) => {
    const chips = page.locator(".session-chip");

    // Fork a second session; the fork becomes the active chip, the primary works on in the background.
    await runCommand(page, "Fork Session");
    await expect(chips).toHaveCount(2);
    await expect(chips.nth(1)).toHaveClass(/active/);

    // Release ONLY the primary's fake ({{WORKSPACE}} is per-session): the background session finishes its
    // turn while the fork stays Working — exactly one attention event, from the background session.
    writeFileSync(join(weavie.workspace, SIGNAL), "");

    await expect.poll(() => notifications(page)).toHaveLength(1);
    expect((await notifications(page))[0].body).toBe("Turn complete — waiting on you.");

    // The differentiator: clicking the notification takes you to THAT session.
    await page.evaluate(() => {
      (window as unknown as RecorderWindow).__notificationClicks[0]();
    });
    await expect(chips.first()).toHaveClass(/active/, { timeout: 15_000 });
  });
});

test.describe("suppression: focused window, active session", () => {
  test.use({
    fakeScript: turnScript({ hook_event_name: "Stop" }),
    preNavigate: recorders({ focused: true }),
  });

  test("the session you're watching never pings", async ({ page, weavie }) => {
    writeFileSync(join(weavie.workspace, SIGNAL), "");

    // The turn observably settled (the shell footer's claude status), so a ping would have fired by now.
    await expect(
      page.locator('.terminal-surface[data-kind="terminal:shell"] .pane-footer'),
    ).toContainText("Idle", { timeout: 20_000 });
    expect(await notifications(page)).toHaveLength(0);
    expect(await sounds(page)).toHaveLength(0);
    expect(await page.title()).not.toMatch(/^●/);
  });
});

test.describe("browser permission is gesture-gated, never requested cold", () => {
  test.use({
    fakeScript: turnScript({ hook_event_name: "Stop" }),
    preNavigate: recorders({ focused: false, permission: "default" }),
  });

  test("the first event raises an Enable toast whose click triggers the real prompt", async ({
    page,
    weavie,
  }) => {
    // Booting alone must not prompt.
    expect(
      await page.evaluate(() => (window as unknown as RecorderWindow).__permissionRequests),
    ).toBe(0);

    writeFileSync(join(weavie.workspace, SIGNAL), "");

    // The event raises the action toast instead of a notification (no permission yet) — the sound still plays.
    const enable = page.locator(".toast-action", { hasText: "Enable" });
    await expect(enable).toBeVisible();
    // The unlock is raised while the user is away, so it must persist: action toasts are exempt from
    // auto-dismiss (no toast-timed drain), staying until acted on.
    await expect(page.locator(".toast", { has: enable })).not.toHaveClass(/toast-timed/);
    expect(await notifications(page)).toHaveLength(0);
    await expect.poll(() => sounds(page)).toHaveLength(1);

    // Clicking the toast button is the user gesture that triggers the real browser prompt.
    await enable.click();
    await expect
      .poll(() => page.evaluate(() => (window as unknown as RecorderWindow).__permissionRequests))
      .toBe(1);
  });
});
