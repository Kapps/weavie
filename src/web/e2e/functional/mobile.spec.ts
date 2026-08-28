import { allowAutomaticInference } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

const PNG_B64 =
  "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAXUlEQVR42u3PMQ0AIAwAMJTsnhxkI2I3NxLQsGNfkxroqohRcWYtAQEBAQEBAQEBAQEBAQEBAQEBAQEBAYF2IPcd9SpHCQgICAgICAgICAgICAgICAgICAgICAi0fZNauTzyRETRAAAAAElFTkSuQmCC";
const sgrPayload = (data: string): string => (data.startsWith("\u001b[") ? data.slice(2) : data);

// Arms a MutationObserver on `target` before the caller triggers the change that may disable it, so a
// disable-then-re-enable cycle that completes faster than Playwright's own DOM polling can still be seen.
// Call this (without awaiting) immediately before the triggering action; await the returned promise after.
function watchForDisabled(target: import("@playwright/test").Locator): Promise<boolean> {
  return target.evaluate(
    (element) =>
      new Promise<boolean>((resolve) => {
        const select = element as HTMLSelectElement;
        if (select.disabled) {
          resolve(true);
          return;
        }
        const observer = new MutationObserver(() => {
          if (select.disabled) {
            observer.disconnect();
            resolve(true);
          }
        });
        observer.observe(select, { attributes: true, attributeFilter: ["disabled"] });
      }),
  );
}

async function pasteImage(target: import("@playwright/test").Locator, b64: string): Promise<void> {
  await target.evaluate((element, encoded) => {
    const binary = atob(encoded);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i += 1) {
      bytes[i] = binary.charCodeAt(i);
    }
    const clipboard = new DataTransfer();
    clipboard.items.add(new File([bytes], "mobile.png", { type: "image/png" }));
    element.dispatchEvent(
      new ClipboardEvent("paste", { clipboardData: clipboard, bubbles: true, cancelable: true }),
    );
  }, b64);
}

async function dispatchPaneTouch(
  target: import("@playwright/test").Locator,
  phase: "touchend" | "touchmove" | "touchstart",
  point: { x: number; y: number },
): Promise<boolean> {
  return target.evaluate(
    (element, touchEvent) => {
      const touch = new Touch({
        identifier: 1,
        target: element,
        clientX: touchEvent.point.x,
        clientY: touchEvent.point.y,
        pageX: touchEvent.point.x,
        pageY: touchEvent.point.y,
        screenX: touchEvent.point.x,
        screenY: touchEvent.point.y,
      });
      const position =
        touchEvent.phase === "touchend"
          ? { changedTouches: [touch] }
          : {
              changedTouches: [touch],
              targetTouches: [touch],
              touches: [touch],
            };
      return element.dispatchEvent(
        new TouchEvent(touchEvent.phase, {
          bubbles: true,
          cancelable: true,
          ...position,
        }),
      );
    },
    { phase, point },
  );
}

async function terminalRows(
  page: import("@playwright/test").Page,
  pane: "claude" | "shell",
): Promise<number> {
  return page.evaluate((targetPane) => {
    const terminal = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(`:${targetPane}`),
    )?.[1];
    if (terminal === undefined) {
      throw new Error(`Missing ${targetPane} terminal`);
    }
    return terminal.rows;
  }, pane);
}

async function pasteThroughNativeTerminalInput(
  page: import("@playwright/test").Page,
  pane: "claude" | "shell",
  text: string,
): Promise<void> {
  const surface = page.locator(`.terminal-surface[data-kind="terminal:${pane}"]`);
  const input = surface.locator(".xterm-helper-textarea");
  await expect(input).toBeVisible();
  const bounds = await input.boundingBox();
  if (bounds === null) {
    throw new Error(`Missing ${pane} terminal input bounds`);
  }
  expect(bounds.width).toBeGreaterThanOrEqual(80);
  expect(bounds.height).toBeGreaterThanOrEqual(32);
  const point = { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 };
  expect(await dispatchPaneTouch(input, "touchstart", point)).toBe(true);
  expect(await dispatchPaneTouch(input, "touchend", point)).toBe(true);
  await input.tap();
  await expect(input).toBeFocused();

  const result = await input.evaluate(
    (element, request) => {
      const terminal = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
        key.endsWith(`:${request.pane}`),
      )?.[1];
      if (terminal === undefined || !(element instanceof HTMLTextAreaElement)) {
        throw new Error(`Missing ${request.pane} terminal input`);
      }
      const input: string[] = [];
      const subscription = terminal.onData((data) => input.push(data));
      const clipboard = new DataTransfer();
      clipboard.setData("text/plain", request.text);
      const clipboardPasteAllowed = element.dispatchEvent(
        new ClipboardEvent("paste", { clipboardData: clipboard, bubbles: true, cancelable: true }),
      );
      element.value = `${request.text} fallback`;
      element.dispatchEvent(
        new InputEvent("input", { bubbles: true, data: null, inputType: "insertFromPaste" }),
      );
      const contextMenuAllowed = element.dispatchEvent(
        new MouseEvent("contextmenu", {
          bubbles: true,
          cancelable: true,
          clientX: request.x,
          clientY: request.y,
        }),
      );
      subscription.dispose();
      return {
        clipboardPasteAllowed,
        contextMenuAllowed,
        data: input,
        value: element.value,
      };
    },
    { pane, text, x: point.x, y: point.y },
  );
  expect(result).toMatchObject({
    clipboardPasteAllowed: false,
    contextMenuAllowed: true,
    value: "",
  });
  expect(result.data).toHaveLength(2);
  expect(result.data[0]).toContain(text);
  expect(result.data[1]).toContain(`${text} fallback`);
}

test.use({
  colorScheme: "light",
  fakeScript: {
    steps: [{ op: "hook", request: { hook_event_name: "SessionStart", source: "startup" } }],
  },
  preNavigate: {
    run: (page) =>
      page.addInitScript(() => {
        Object.defineProperty(navigator, "standalone", { configurable: true, value: true });
        if (window.visualViewport !== null) {
          Object.defineProperty(window.visualViewport, "height", {
            configurable: true,
            value: window.innerHeight - 64,
          });
        }
      }),
  },
});

const NAMEABLE_DRAFT =
  "the mobile branch inference field should fill itself from the prompt the user has just written here";
const VAGUE_DRAFT =
  "something in the mobile inbox is not right and it would be good if someone looked at it";

test.describe("configured branch inference", () => {
  test.use({ inference: "success", automaticInference: true });

  test("fills the compact branch field with the inferred name", async ({ page }) => {
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
    await inbox.getByRole("textbox", { name: "Prompt for a new session" }).fill(NAMEABLE_DRAFT);

    await expect(inbox.getByRole("textbox", { name: "Branch for the new session" })).toHaveValue(
      "fix/mobile-branch-inference",
    );
    await expect(inbox.getByRole("alert")).toHaveCount(0);
  });
});

test.describe("vague branch inference", () => {
  test.use({ inference: "needsDetail", automaticInference: true });

  test("keeps listening until the prompt names a task", async ({ page }) => {
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
    const composer = inbox.getByRole("textbox", { name: "Prompt for a new session" });
    await composer.fill(VAGUE_DRAFT);

    const branch = inbox.getByRole("textbox", { name: "Branch for the new session" });
    await expect(branch).toHaveAttribute("placeholder", "Say more, or type a name");

    await composer.fill(`${VAGUE_DRAFT} when the compact branch field is used`);
    await expect(branch).toHaveValue("fix/mobile-branch-inference");
    await expect(inbox.getByRole("alert")).toHaveCount(0);
  });
});

test.describe("re-suggesting a branch", () => {
  test.use({ inference: "success", automaticInference: true });

  test("replaces a typed name when asked for another suggestion", async ({ page }) => {
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
    await inbox.getByRole("textbox", { name: "Prompt for a new session" }).fill(NAMEABLE_DRAFT);

    const branch = inbox.getByRole("textbox", { name: "Branch for the new session" });
    await expect(branch).toHaveValue("fix/mobile-branch-inference");

    await branch.fill("mine/hand-written");
    await inbox.getByRole("button", { name: "Suggest a branch name again" }).click();
    await expect(branch).toHaveValue("fix/mobile-branch-inference");
  });
});

test.describe("failed branch inference", () => {
  test.use({ inference: "failure", automaticInference: true });

  test("leaves the branch blank and requires manual input", async ({ page }) => {
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
    await inbox.getByRole("textbox", { name: "Prompt for a new session" }).fill(NAMEABLE_DRAFT);

    const branch = inbox.getByRole("textbox", { name: "Branch for the new session" });
    await expect(branch).toHaveValue("");
    await expect(inbox.getByRole("alert")).toHaveText(
      "Branch suggestion failed: Claude inference exited with code 7. Type a branch to continue.",
    );
    await expect(inbox.getByRole("button", { name: "Start", exact: true })).toBeDisabled();
    await branch.fill("fix/manual-branch-inference");
    await expect(inbox.getByRole("button", { name: "Start", exact: true })).toBeEnabled();
  });
});

test.describe("automatic inference permission", () => {
  test.use({
    inference: "success",
    automaticInference: false,
    dismissInferenceOffer: false,
  });

  test("allows inference from the notification before suggesting a branch", async ({ page }) => {
    await allowAutomaticInference(page);

    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
    await inbox.getByRole("textbox", { name: "Prompt for a new session" }).fill(NAMEABLE_DRAFT);

    await expect(inbox.getByRole("textbox", { name: "Branch for the new session" })).toHaveValue(
      "fix/mobile-branch-inference",
    );
  });
});

test("focusing the recent-files search keeps the app viewport fixed", async ({ page }) => {
  const viewport = await page.locator('meta[name="viewport"]').getAttribute("content");
  expect(viewport).toContain("maximum-scale=1");
  expect(viewport).toContain("user-scalable=no");

  await page.getByRole("button", { name: "Code", exact: true }).click();
  await page.getByRole("button", { name: "Recent", exact: true }).click();

  await expect(page.getByPlaceholder("Search recent files…")).toBeFocused();
});

test("the software keyboard keeps Claude reachable without scrolling the document", async ({
  page,
}) => {
  await page.locator(".session-inbox-row").click();

  const terminal = page.locator('.terminal-surface[data-kind="terminal:claude"]');
  const screen = terminal.locator(".xterm-screen");
  await expect(terminal).toBeVisible();
  await expect(screen).toBeVisible();

  const bounds = await screen.boundingBox();
  if (bounds === null) {
    throw new Error("Missing Claude terminal bounds");
  }
  await page.touchscreen.tap(bounds.x + 40, bounds.y + bounds.height - 24);

  await expect
    .poll(() =>
      terminal
        .locator(".xterm-helper-textarea")
        .evaluate((element) => document.activeElement === element),
    )
    .toBe(true);

  const initialViewport = await page.evaluate(() => ({
    innerHeight: window.innerHeight,
    visualHeight: window.visualViewport?.height,
  }));
  if (initialViewport.visualHeight === undefined) {
    throw new Error("VisualViewport is unavailable");
  }
  const initialRows = await terminalRows(page, "claude");
  await page.evaluate(() => {
    const viewport = window.visualViewport;
    if (viewport === null) {
      throw new Error("VisualViewport is unavailable");
    }
    const spacer = document.createElement("div");
    spacer.style.cssText = "position:absolute;top:0;width:1px;height:1400px";
    document.body.append(spacer);
    document.documentElement.style.overflow = "visible";
    document.body.style.overflow = "visible";
    Object.defineProperties(viewport, {
      height: { configurable: true, value: 500 },
      offsetTop: { configurable: true, value: 24 },
    });
    window.dispatchEvent(new Event("resize"));
    viewport.dispatchEvent(new Event("resize"));
    viewport.dispatchEvent(new Event("scroll"));
    window.scrollTo(0, 280);
    if (window.scrollY !== 280) {
      throw new Error(`Could not simulate the keyboard's document scroll: ${window.scrollY}`);
    }
  });

  await expect
    .poll(() =>
      page.locator(".app").evaluate((app) => {
        const bounds = app.getBoundingClientRect();
        return { bottom: bounds.bottom, height: bounds.height, top: bounds.top };
      }),
    )
    .toEqual({ bottom: 524, height: 500, top: 24 });
  const keyboardGeometry = await page.evaluate(() => {
    const app = document.querySelector(".app")?.getBoundingClientRect();
    const nav = document.querySelector(".mobile-surface-bar")?.getBoundingClientRect();
    const pane = document.querySelector(".pane-area")?.getBoundingClientRect();
    return {
      appBottom: app?.bottom,
      navBottom: nav?.bottom,
      paneBottom: pane?.bottom,
      scrollingElementTop: document.scrollingElement?.scrollTop,
      scrollY: window.scrollY,
    };
  });
  expect(keyboardGeometry.appBottom).toBe(524);
  expect(keyboardGeometry.navBottom).toBe(524);
  expect(keyboardGeometry.paneBottom).toBeLessThan(524);
  expect(keyboardGeometry.scrollingElementTop).toBe(0);
  expect(keyboardGeometry.scrollY).toBe(0);
  await expect.poll(() => terminalRows(page, "claude")).toBeLessThan(initialRows);

  const keyboardRows = await terminalRows(page, "claude");
  await page.evaluate((initial) => {
    const viewport = window.visualViewport;
    if (viewport === null) {
      throw new Error("VisualViewport is unavailable");
    }
    Object.defineProperties(viewport, {
      height: { configurable: true, value: initial.visualHeight },
      offsetTop: { configurable: true, value: 0 },
    });
    window.dispatchEvent(new Event("resize"));
    viewport.dispatchEvent(new Event("resize"));
    viewport.dispatchEvent(new Event("scroll"));
  }, initialViewport);

  await expect
    .poll(() =>
      page.locator(".app").evaluate((app) => {
        const bounds = app.getBoundingClientRect();
        return { bottom: bounds.bottom, height: bounds.height, top: bounds.top };
      }),
    )
    .toEqual({ bottom: initialViewport.innerHeight, height: initialViewport.innerHeight, top: 0 });
  await expect.poll(() => terminalRows(page, "claude")).toBeGreaterThan(keyboardRows);
});

// 2026-08-13: flaked on the macOS shard — https://github.com/Kapps/weavie/actions/runs/31660811208/job/94325711777.
// Root cause: xterm.js's own "contextmenu" listener unconditionally loads the clicked word into the same
// hidden textarea our native paste handling clears (desktop copy-then-paste-over-selection), and defaults
// `rightClickSelectsWord` to on for any Mac-family `navigator.platform` — true on the macOS CI runner's
// Chromium (and real iPad Safari, the actual native-touch-paste device). The shell pane's real PTY prompt
// text sat under the enlarged touch-paste hit target's tap point, so xterm's handler clobbered the textarea
// right after `onNativePasteInput` (TerminalView.tsx) had cleared it — a real product defect, not test
// timing. Fixed in TerminalView.tsx by disabling `rightClickSelectsWord` for native-touch-paste terminals.
// A synthetic regression test that spoofed `navigator.platform` to force this deterministically was tried
// and reverted: it reproduced fine against the sandbox's Linux harness but hung the full 30s timeout on
// both the Windows and macOS shards even after fixing its own prompt-detection race, most likely because
// spoofing the client's reported platform independently of the real OS interferes with the host's real
// platform-specific terminal negotiation (e.g. win32-input-mode) — a confound the sandbox can't reproduce.
// This test below already exercises the real bug end to end and is the regression coverage for it.
test("the terminal cursor exposes native paste in both terminal panes", async ({ page }) => {
  await page.locator(".session-inbox-row").click();
  expect(await page.evaluate(() => matchMedia("(pointer: coarse)").matches)).toBe(true);

  await pasteThroughNativeTerminalInput(page, "claude", "claude paste");
  await page.getByRole("button", { name: "Shell" }).click();
  await pasteThroughNativeTerminalInput(page, "shell", "shell paste");
});

test("touch scrolling and tapping a mouse-aware Claude prompt send valid input", async ({
  page,
}) => {
  await page.locator(".session-inbox-row").click();
  const terminal = page.locator('.terminal-surface[data-kind="terminal:claude"]');
  const screen = terminal.locator(".xterm-screen");
  await expect(screen).toBeVisible();

  await screen.evaluate(async (element) => {
    const terminal = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(":claude"),
    )?.[1];
    if (terminal === undefined) {
      throw new Error("Missing Claude terminal");
    }
    const input: string[] = [];
    const subscription = terminal.onData((data) => input.push(data));
    Object.assign(element, {
      __weavieTouchInput: { dispose: () => subscription.dispose(), input },
    });
    await new Promise<void>((resolve) => terminal.write("\x1b[?1000h\x1b[?1006h", resolve));
  });

  const bounds = await screen.boundingBox();
  if (bounds === null) {
    throw new Error("Missing Claude terminal bounds");
  }
  const x = bounds.x + bounds.width / 2;
  const startY = bounds.y + bounds.height * 0.65;
  const endY = startY - 60;
  const readInput = (): Promise<string[]> =>
    screen.evaluate((element) => {
      const capture = (
        element as Element & { __weavieTouchInput?: { dispose: () => void; input: string[] } }
      ).__weavieTouchInput;
      if (capture === undefined) {
        throw new Error("Missing terminal input capture");
      }
      return [...capture.input];
    });
  await page.touchscreen.tap(x, startY);

  await expect(terminal.locator(".xterm-helper-textarea")).toBeFocused();
  await expect
    .poll(async () => (await readInput()).map(sgrPayload).filter((data) => data.startsWith("<")))
    .toEqual([expect.stringMatching(/^<0;\d+;\d+M$/), expect.stringMatching(/^<0;\d+;\d+m$/)]);
  await screen.evaluate((element) => {
    const capture = (
      element as Element & { __weavieTouchInput?: { dispose: () => void; input: string[] } }
    ).__weavieTouchInput;
    if (capture === undefined) {
      throw new Error("Missing terminal input capture");
    }
    capture.input.length = 0;
  });

  await dispatchPaneTouch(screen, "touchstart", { x, y: startY });
  await page.evaluate(() => new Promise(requestAnimationFrame));
  await dispatchPaneTouch(screen, "touchmove", { x, y: endY });
  await page.evaluate(() => new Promise(requestAnimationFrame));
  await screen.evaluate((element) => {
    const capture = (
      element as Element & { __weavieTouchInput?: { dispose: () => void; input: string[] } }
    ).__weavieTouchInput;
    if (capture === undefined) {
      throw new Error("Missing terminal input capture");
    }
    capture.input.length = 0;
  });
  await dispatchPaneTouch(screen, "touchend", { x, y: endY });
  await page.evaluate(
    () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))),
  );

  const scrollInput = await readInput();
  expect(scrollInput.length).toBeGreaterThan(0);
  expect(scrollInput.join("")).not.toContain("NaN");
  expect(scrollInput.every((data) => /^<(64|65);\d+;\d+M$/.test(sgrPayload(data)))).toBe(true);
  await screen.evaluate((element) => {
    const capture = (
      element as Element & { __weavieTouchInput?: { dispose: () => void; input: string[] } }
    ).__weavieTouchInput;
    capture?.dispose();
    delete (element as Element & { __weavieTouchInput?: { dispose: () => void; input: string[] } })
      .__weavieTouchInput;
  });
});

test("Claude Code accepts back swipes beside the screen edge, never on it", async ({ page }) => {
  await page.locator(".session-inbox-row").click();
  const terminal = page.locator('.terminal-surface[data-kind="terminal:claude"]');
  const body = terminal.locator(".xterm-screen");
  await expect(body).toBeVisible();

  await dispatchPaneTouch(body, "touchstart", { x: 100, y: 240 });
  await dispatchPaneTouch(body, "touchmove", { x: 240, y: 240 });
  await dispatchPaneTouch(body, "touchend", { x: 240, y: 240 });
  await expect(terminal).toBeVisible();
  await expect(page.locator(".session-inbox")).toBeHidden();

  await dispatchPaneTouch(body, "touchstart", { x: 16, y: 240 });
  await dispatchPaneTouch(body, "touchmove", { x: 156, y: 240 });
  await dispatchPaneTouch(body, "touchend", { x: 156, y: 240 });
  await expect(terminal).toBeVisible();
  await expect(page.locator(".session-inbox")).toBeHidden();

  await dispatchPaneTouch(body, "touchstart", { x: 48, y: 240 });
  await dispatchPaneTouch(body, "touchmove", { x: 188, y: 240 });
  await dispatchPaneTouch(body, "touchend", { x: 188, y: 240 });
  await expect(page.locator(".session-inbox")).toBeVisible();
});

// Touch chrome hides the session rail, so the inbox row is where a session is managed: hold it (the
// stand-in for right-click) or tap its actions button, and the entries are the rail's own command rows.
test("a compact session row manages its session from a hold and its actions button", async ({
  page,
}) => {
  const inbox = page.locator(".session-inbox");
  const row = inbox.locator(".session-inbox-row").first();
  const menu = page.locator(".context-menu");
  const manage = row.getByRole("button", { name: /^Manage / });
  await expect(row).toBeVisible();

  const bounds = await row.boundingBox();
  if (bounds === null) {
    throw new Error("Missing session row bounds");
  }
  const touch = await page.context().newCDPSession(page);
  const point = { x: bounds.x + 60, y: bounds.y + bounds.height / 2 };
  const hold = async (): Promise<void> => {
    await touch.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [point] });
    await expect(menu).toBeVisible();
  };

  // A press that drifts is the user scrolling the list, so it must never arm the menu. Only a real wait past
  // the hold deadline can prove "never opens" — an immediate assertion would pass before the timer fires.
  await touch.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [point] });
  for (const dy of [12, 30, 48]) {
    await touch.send("Input.dispatchTouchEvent", {
      type: "touchMove",
      touchPoints: [{ x: point.x, y: point.y - dy }],
    });
  }
  await page.waitForTimeout(800);
  await expect(menu).toHaveCount(0);
  await touch.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
  await expect(inbox).toBeVisible();

  await hold();
  await touch.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });

  // The click the release synthesizes lands on the menu that just appeared under the finger — it must not
  // run one of its rows, nor fall through to the row and open the session.
  await expect(menu).toBeVisible();
  await expect(page.locator(".confirm-dialog")).toHaveCount(0);
  await expect(inbox).toBeVisible();
  await expect(menu.locator(".context-menu-item")).toHaveText(["Unload session", "Delete…"]);
  const rowHeights = await menu
    .locator(".context-menu-item")
    .evaluateAll((items) => items.map((item) => item.getBoundingClientRect().height));
  expect(Math.min(...rowHeights)).toBeGreaterThanOrEqual(44);

  await menu.locator(".context-menu-item", { hasText: "Unload session" }).click();
  await expect(row.locator(".session-inbox-state")).toHaveText("Unloaded");

  // Sliding off the row before lifting cancels the touch, which synthesizes no click at all — so nothing may
  // stay armed to eat the tap that follows.
  await hold();
  await touch.send("Input.dispatchTouchEvent", {
    type: "touchMove",
    touchPoints: [{ x: point.x, y: point.y + 60 }],
  });
  await touch.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
  await expect(menu.locator(".context-menu-item")).toHaveText(["Load session", "Delete…"]);
  await menu.locator(".context-menu-item", { hasText: "Load session" }).click();
  await expect(row.locator(".session-inbox-state")).not.toHaveText("Unloaded");

  await manage.click();
  await menu.locator(".context-menu-item.danger", { hasText: "Delete" }).click();
  const dialog = page.locator(".confirm-dialog");
  await expect(dialog).toBeVisible();
  await dialog.locator(".confirm-btn-danger").click();
  await expect(page.locator(".toast", { hasText: "was deleted." })).toHaveCount(1);
});

test("WebM video opens inline in the compact editor", async ({ page }) => {
  await page.getByRole("button", { name: "Code", exact: true }).click();
  await page.getByRole("button", { name: "Files" }).click();
  await page.locator(".browser-row", { hasText: "clip.webm" }).click();
  await page.getByTitle("Close (Esc)").click();

  const video = page.locator(".editor-media video");
  await expect(video).toBeVisible();
  await expect(video).toHaveAttribute("src", /\/weavie-media\/clip\.webm\?/);
  await expect(video).toHaveAttribute("playsinline", "");
  await expect(video).toHaveAttribute("controls", "");
  await expect(video).toHaveAttribute("preload", "metadata");
  await expect
    .poll(() => video.evaluate((element) => element.readyState))
    .toBeGreaterThanOrEqual(1);
  await expect(page.locator(".editor-media-notice")).toHaveCount(0);
});

test("compact session inbox creates, resumes, and switches existing surfaces", async ({ page }) => {
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  const inbox = page.locator(".session-inbox");
  const newSessionPrompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
  await expect(inbox).toBeVisible();
  await expect(inbox.locator(".session-inbox-row")).toHaveCount(1);

  const geometry = await page.evaluate(() => {
    const bounds = (selector: string): DOMRect => {
      const element = document.querySelector(selector);
      if (!(element instanceof HTMLElement)) {
        throw new Error(`Missing ${selector}`);
      }
      return element.getBoundingClientRect();
    };
    const app = bounds(".app");
    const pane = bounds(".pane-area");
    const nav = bounds(".mobile-surface-bar");
    return {
      appBottom: app.bottom,
      navHeight: nav.height,
      paneBottom: pane.bottom,
      navBottom: nav.bottom,
      navPaddingBottom: Number.parseFloat(
        getComputedStyle(document.querySelector(".mobile-surface-bar")!).paddingBottom,
      ),
      navTop: nav.top,
      visualViewportHeight: window.visualViewport?.height,
      viewportHeight: window.innerHeight,
    };
  });
  expect(geometry.visualViewportHeight).toBeLessThan(geometry.viewportHeight);
  expect(geometry.appBottom).toBe(geometry.viewportHeight);
  expect(geometry.navBottom).toBe(geometry.viewportHeight);
  expect(geometry.navPaddingBottom).toBe(10);
  expect(geometry.navHeight).toBe(60);
  expect(geometry.paneBottom).toBe(geometry.navTop);

  await newSessionPrompt.fill("Keep this draft");
  await newSessionPrompt.press("Enter");
  await expect(newSessionPrompt).toHaveValue("Keep this draft\n");
  await expect(inbox.locator(".session-inbox-row")).toHaveCount(1);

  await newSessionPrompt.fill("Improve mobile navigation");
  await pasteImage(newSessionPrompt, PNG_B64);
  await expect(inbox.locator(".agent-attachment img")).toBeVisible();
  const provider = inbox.getByRole("combobox", { name: "Agent provider" });
  // The save round-trip that backs the disable can resolve inside a single DOM-poll interval, so polling
  // for `toBeDisabled()` after the fact can miss it entirely — see the flake note below. Arm a
  // MutationObserver before triggering the change instead, so the transition can't be missed regardless
  // of how fast the host responds.
  //
  // Flaked 2026-08-27 21:39 UTC on macOS shard 6/6 — expect(provider).toBeDisabled() saw "enabled" on all
  // 63 of its polls across the full 30s timeout (https://github.com/Kapps/weavie/actions/runs/33118598692/job/98680314997),
  // meaning the select had already re-enabled before Playwright's first poll ran. Replaced the DOM-polling
  // assertion with the observer above.
  const sawDisabled = watchForDisabled(provider);
  await provider.selectOption("fake-acp");
  expect(await sawDisabled).toBe(true);
  await expect(provider).toBeEnabled();
  await expect(provider).toHaveValue("fake-acp");
  await expect(inbox.getByRole("combobox", { name: "Open with" })).toHaveValue("fake-acp");
  const startButton = inbox.getByRole("button", { name: "Start", exact: true });
  const branch = inbox.getByRole("textbox", { name: "Branch for the new session" });
  await expect(branch).toHaveValue("");
  // A draft this short earns no automatic query, so leaving the prompt is what attempts the name.
  await newSessionPrompt.focus();
  await inbox.getByRole("combobox", { name: "Session location" }).focus();
  await expect(inbox.getByRole("alert")).toHaveText(
    "Branch suggestion failed: Ad-hoc inference is disabled. Type a branch to continue.",
  );
  await branch.fill("bug/mobile-navigation");
  await expect(startButton).toBeEnabled();
  const primaryColors = await semanticButtonColors(page);
  await expect(startButton).toHaveCSS("background-color", primaryColors.background);
  await expect(startButton).toHaveCSS("color", primaryColors.foreground);
  const inboxHistoryLength = await page.evaluate(() => history.length);
  await newSessionPrompt.focus();
  await newSessionPrompt.press("Shift+Enter");

  await expect(inbox).toBeHidden();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  const agentSurface = page.locator(".agent-surface");
  await expect(agentSurface).toBeVisible();
  await expect(agentSurface).toContainText("echo: Improve mobile navigation");
  expect(await page.evaluate(() => history.length)).toBe(inboxHistoryLength + 1);

  const activeComposer = agentSurface.locator("[data-agent-composer] textarea");
  await activeComposer.fill("First line");
  await activeComposer.press("Enter");
  await expect(activeComposer).toHaveValue("First line\n");
  await expect(agentSurface).not.toContainText("echo: First line");
  const runButton = agentSurface.getByRole("button", { name: "Run", exact: true });
  await expect(runButton).toHaveCSS("background-color", primaryColors.background);
  await expect(runButton).toHaveCSS("color", primaryColors.foreground);
  await runButton.click();
  await expect(agentSurface).toContainText("echo: First line");

  const compactChrome = await agentSurface.evaluate((surface) => {
    const composer = surface.querySelector("[data-agent-composer]")?.getBoundingClientRect();
    const header = surface.querySelector(".pane-head")?.getBoundingClientRect();
    return {
      composerHeight: composer?.height ?? Number.POSITIVE_INFINITY,
      hasFooterStatus: surface.querySelector(":scope > .agent-status-line") !== null,
      headerHeight: header?.height ?? Number.POSITIVE_INFINITY,
      statusInHeader: surface.querySelector(".pane-head .agent-status-line") !== null,
    };
  });
  expect(compactChrome.composerHeight).toBeLessThanOrEqual(60);
  expect(compactChrome.headerHeight).toBeLessThanOrEqual(44);
  expect(compactChrome.hasFooterStatus).toBe(false);
  expect(compactChrome.statusInHeader).toBe(true);

  await page.evaluate(() => history.back());
  await expect(inbox).toBeVisible();
  await expect(
    inbox.locator(".session-inbox-row", { hasText: "bug/mobile-navigation" }),
  ).toHaveCount(1);
  await page.evaluate(() => history.forward());
  await expect(agentSurface).toBeVisible();

  await page.getByRole("button", { name: "Shell" }).click();
  await expect(page.locator(".terminal-surface[data-kind='terminal:shell']")).toBeVisible();

  await page.getByRole("button", { name: "Code" }).click();
  await expect(page.locator(".editor-surface")).toBeVisible();
  await page.getByRole("button", { name: "Files" }).click();
  const fileRow = page.locator(".browser-row", { hasText: "hello.ts" });
  const browserClose = page.getByTitle("Close (Esc)");
  const [fileRowBox, browserCloseBox] = await Promise.all([
    fileRow.boundingBox(),
    browserClose.boundingBox(),
  ]);
  expect(fileRowBox?.height).toBeGreaterThanOrEqual(44);
  expect(browserCloseBox?.width).toBeGreaterThanOrEqual(44);
  expect(browserCloseBox?.height).toBeGreaterThanOrEqual(44);
  await fileRow.click();
  await browserClose.click();
  await expect(page.locator(".monaco-editor")).toBeVisible();

  expect(await page.evaluate(() => history.length)).toBe(inboxHistoryLength + 1);
  await page.evaluate(() => history.back());
  await expect(inbox).toBeVisible();
  await page.evaluate(() => history.forward());
  await expect(page.locator(".editor-surface")).toBeVisible();

  await page.getByRole("button", { name: "Agent" }).click();
  const scrollerMoveAccepted = await agentSurface.evaluate((surface) => {
    const body = surface.querySelector(".agent-body");
    if (!(body instanceof HTMLElement)) {
      throw new Error("Missing agent body");
    }
    const scroller = document.createElement("div");
    scroller.style.cssText = "width:100px;overflow-x:auto";
    scroller.innerHTML = '<div style="width:1000px;height:1px"></div>';
    body.append(scroller);
    const touch = (clientX: number) =>
      new Touch({ identifier: 1, target: scroller, clientX, clientY: 240 });
    scroller.dispatchEvent(
      new TouchEvent("touchstart", {
        bubbles: true,
        touches: [touch(90)],
        targetTouches: [touch(90)],
      }),
    );
    const moveAccepted = scroller.dispatchEvent(
      new TouchEvent("touchmove", {
        bubbles: true,
        cancelable: true,
        touches: [touch(270)],
        targetTouches: [touch(270)],
      }),
    );
    scroller.dispatchEvent(
      new TouchEvent("touchend", { bubbles: true, changedTouches: [touch(270)] }),
    );
    scroller.remove();
    return moveAccepted;
  });
  expect(scrollerMoveAccepted).toBe(true);

  const agentBody = agentSurface.locator(".agent-body");
  await dispatchPaneTouch(agentBody, "touchstart", { x: 300, y: 240 });
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 120, y: 240 })).toBe(true);
  await dispatchPaneTouch(agentBody, "touchend", { x: 120, y: 240 });
  await expect(agentSurface).toBeVisible();
  await expect(inbox).toBeHidden();
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);
  await expect(page.locator(".pane-area")).toHaveCSS("transform", "none");

  await dispatchPaneTouch(agentBody, "touchstart", { x: 80, y: 240 });
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 88, y: 243 })).toBe(true);
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 92, y: 300 })).toBe(true);
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 220, y: 310 })).toBe(true);
  await dispatchPaneTouch(agentBody, "touchend", { x: 270, y: 310 });
  await expect(agentSurface).toBeVisible();
  await expect(inbox).toBeHidden();
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);

  await dispatchPaneTouch(activeComposer, "touchstart", { x: 80, y: 240 });
  expect(await dispatchPaneTouch(activeComposer, "touchmove", { x: 220, y: 240 })).toBe(true);
  await dispatchPaneTouch(activeComposer, "touchend", { x: 220, y: 240 });
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);

  await activeComposer.fill("Open ./hello.ts");
  await agentSurface.getByRole("button", { name: "Run", exact: true }).click();
  const agentFileLink = agentSurface.getByRole("link", { name: "./hello.ts", exact: true }).last();
  await expect(agentFileLink).toBeVisible();
  const beforeFileNavigation = await page.evaluate(() => history.length);
  await agentFileLink.click();
  const editorSurface = page.locator(".editor-surface");
  await expect(editorSurface).toBeVisible();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");
  expect(await page.evaluate(() => history.length)).toBe(beforeFileNavigation + 1);
  expect(
    await page.evaluate(
      () =>
        (
          history.state as {
            __weavieMobileNavigation?: { stack?: unknown };
          }
        ).__weavieMobileNavigation?.stack,
    ),
  ).toEqual(["inbox", "terminal:claude", "editor"]);

  const editorChrome = editorSurface.locator(".editor-tabs");
  await dispatchPaneTouch(editorChrome, "touchstart", { x: 80, y: 240 });
  expect(await dispatchPaneTouch(editorChrome, "touchmove", { x: 120, y: 240 })).toBe(false);
  await expect(agentSurface).toBeVisible();
  await dispatchPaneTouch(editorChrome, "touchend", { x: 120, y: 240 });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);

  await page.evaluate(() => history.back());
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  await page.reload();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

  await page.getByRole("button", { name: "Shell" }).click();
  const shellSurface = page.locator(".terminal-surface[data-kind='terminal:shell']");
  await expect(shellSurface).toBeVisible();
  expect(
    await page.evaluate(
      () =>
        (
          history.state as {
            __weavieMobileNavigation?: { stack?: unknown };
          }
        ).__weavieMobileNavigation?.stack,
    ),
  ).toEqual(["inbox", "terminal:claude", "terminal:shell"]);
  await page.goForward();
  await expect(shellSurface).toBeVisible();

  const shellChrome = shellSurface.locator(".pane-head");
  await dispatchPaneTouch(shellChrome, "touchstart", { x: 80, y: 240 });
  await dispatchPaneTouch(shellChrome, "touchmove", { x: 220, y: 240 });
  await expect(agentSurface).toBeVisible();
  await dispatchPaneTouch(shellChrome, "touchend", { x: 270, y: 240 });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

  await agentFileLink.click();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");

  // The browser navigating mid-swipe — its own edge gesture, the OS back button — takes the surface the
  // transition was moving off, so the transition goes rather than committing a second move on top of it.
  await dispatchPaneTouch(editorChrome, "touchstart", { x: 80, y: 240 });
  await dispatchPaneTouch(editorChrome, "touchmove", { x: 220, y: 240 });
  await expect(page.locator(".app.mobile-transition")).toHaveCount(1);
  await page.evaluate(() => history.back());
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);
  await dispatchPaneTouch(editorChrome, "touchend", { x: 270, y: 240 });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  await expect(inbox).toBeHidden();
  await page.goForward();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");

  await dispatchPaneTouch(editorChrome, "touchstart", { x: 80, y: 240 });
  await dispatchPaneTouch(editorChrome, "touchmove", { x: 220, y: 240 });
  await expect(agentSurface).toBeVisible();
  const layout = page.locator(".layout-root");
  await expect(layout).not.toHaveCSS("transform", "none");
  const draggedRight = (await layout.boundingBox())!.x;
  await dispatchPaneTouch(editorChrome, "touchmove", { x: 120, y: 240 });
  expect((await layout.boundingBox())!.x).toBeLessThan(draggedRight);
  await dispatchPaneTouch(editorChrome, "touchmove", { x: 270, y: 240 });
  await dispatchPaneTouch(editorChrome, "touchend", { x: 270, y: 240 });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  await expect(agentSurface).toBeVisible();

  // A swipe off the screen edge is iOS's own back gesture, which pops the same history Weavie navigates:
  // tracking it here as well left the touch driving two back navigations at once.
  await dispatchPaneTouch(agentBody, "touchstart", { x: 12, y: 240 });
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 220, y: 240 })).toBe(true);
  await dispatchPaneTouch(agentBody, "touchend", { x: 270, y: 240 });
  await expect(inbox).toBeHidden();
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);

  await dispatchPaneTouch(agentBody, "touchstart", { x: 80, y: 240 });
  await dispatchPaneTouch(agentBody, "touchmove", { x: 220, y: 240 });
  await expect(inbox).toBeVisible();
  await dispatchPaneTouch(agentBody, "touchend", { x: 270, y: 240 });
  await expect(inbox).toBeVisible();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Sessions");

  await expect(inbox.locator(".session-inbox-row")).toHaveCount(2);
  await expect(inbox).toContainText("bug/mobile-navigation");
  await inbox.locator(".session-inbox-row").first().click();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

  const bar = page.locator(".mobile-surface-bar");
  // The bar swipes both ways and reaches both screen edges, which the browser navigates history from.
  for (const edge of [12, 378]) {
    await bar.dispatchEvent("pointerdown", {
      clientX: edge,
      clientY: 20,
      pointerId: 0,
      pointerType: "touch",
    });
    await bar.dispatchEvent("pointermove", {
      clientX: 195,
      clientY: 20,
      pointerId: 0,
      pointerType: "touch",
    });
    await bar.dispatchEvent("pointerup", {
      clientX: 195,
      clientY: 20,
      pointerId: 0,
      pointerType: "touch",
    });
    await expect(page.locator(".app.mobile-transition")).toHaveCount(0);
    await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  }

  await bar.dispatchEvent("pointerdown", {
    clientX: 300,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  await bar.dispatchEvent("pointermove", {
    clientX: 220,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  await expect(page.locator(".terminal-surface[data-kind='terminal:shell']")).toBeVisible();
  await expect(layout).not.toHaveCSS("transform", "none");
  const draggedLeft = (await layout.boundingBox())!.x;
  await bar.dispatchEvent("pointermove", {
    clientX: 290,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  expect((await layout.boundingBox())!.x).toBeGreaterThan(draggedLeft);
  await bar.dispatchEvent("pointerup", {
    clientX: 120,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Shell");
  await expect(bar).toBeFocused();

  await page.getByRole("button", { name: "Sessions" }).click();
  await inbox.locator(".session-inbox-row", { hasText: "bug/mobile-navigation" }).click();
  await expect(agentSurface).toBeVisible();

  await agentSurface.evaluate((element) => {
    (window as Window & { __breakpointProof?: Element }).__breakpointProof = element;
  });

  const horizontalContainment = await agentSurface.evaluate((surface) => {
    const body = surface.querySelector(".agent-body");
    const pane = surface.closest(".pane-area");
    if (!(body instanceof HTMLElement) || !(pane instanceof HTMLElement)) {
      throw new Error("Missing agent scroll containers");
    }
    const wide = document.createElement("div");
    wide.style.width = "1600px";
    wide.style.height = "1px";
    body.append(wide);
    const result = {
      bodyOverflowX: getComputedStyle(body).overflowX,
      documentContained: document.documentElement.scrollWidth === window.innerWidth,
      paneContained: pane.scrollWidth === pane.clientWidth,
    };
    wide.remove();
    return result;
  });
  expect(horizontalContainment.bodyOverflowX).toBe("hidden");
  expect(horizontalContainment.documentContained).toBe(true);
  expect(horizontalContainment.paneContained).toBe(true);

  await page.setViewportSize({ width: 900, height: 844 });
  await expect(page.locator(".session-rail")).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "Agent" }).click();
  expect(
    await agentSurface.evaluate(
      (element) =>
        (window as Window & { __breakpointProof?: Element }).__breakpointProof === element,
    ),
  ).toBe(true);
});

async function semanticButtonColors(
  page: import("@playwright/test").Page,
): Promise<{ background: string; foreground: string }> {
  return page.evaluate(() => {
    const probe = document.createElement("span");
    probe.style.background = "var(--weavie-button-background)";
    probe.style.color = "var(--weavie-button-foreground)";
    document.body.append(probe);
    const style = getComputedStyle(probe);
    const colors = { background: style.backgroundColor, foreground: style.color };
    probe.remove();
    return colors;
  });
}
