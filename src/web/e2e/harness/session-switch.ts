import type { Page } from "@playwright/test";

export interface SessionSwitchExpectation {
  label: string;
  provider: "claude" | "codex";
  tabs: string[];
  activeTab: string;
  content:
    | { kind: "text"; pathSuffix: string; marker: string }
    | { kind: "image"; pathSuffix: string; sessionId: string };
}

/** Clicks a session chip and measures until its provider, tabs, and active editor content have painted. */
export async function measureSessionSwitch(
  page: Page,
  expected: SessionSwitchExpectation,
): Promise<number> {
  return page.evaluate(async (target) => {
    const chip = [...document.querySelectorAll<HTMLButtonElement>(".session-chip")].find((item) =>
      item.title.startsWith(`${target.label} —`),
    );
    if (chip === undefined) {
      throw new Error(`missing session chip ${target.label}`);
    }

    const normalized = (path: string): string => path.replaceAll("\\", "/");
    const complete = (): boolean => {
      const activeChip = document.querySelector<HTMLButtonElement>(".session-chip.active");
      if (activeChip?.title.startsWith(`${target.label} —`) !== true) {
        return false;
      }
      const surface = document.querySelector<HTMLElement>('[data-kind="terminal:claude"]');
      const wantedSurface = target.provider === "codex" ? "structured-agent" : "terminal";
      if (surface?.dataset.surface !== wantedSurface) {
        return false;
      }
      const tabs = [...document.querySelectorAll<HTMLElement>(".editor-tab .editor-tab-label")];
      if (
        tabs.length !== target.tabs.length ||
        tabs.some((tab, index) => tab.textContent !== target.tabs[index]) ||
        document.querySelector(".editor-tab.active .editor-tab-label")?.textContent !==
          target.activeTab
      ) {
        return false;
      }
      if (target.content.kind === "text") {
        const activeFile = document.querySelector<HTMLElement>(".editor")?.dataset.activeFile;
        return (
          activeFile !== undefined &&
          normalized(activeFile).endsWith(target.content.pathSuffix) &&
          document
            .querySelector(".monaco-editor .view-lines")
            ?.textContent?.includes(target.content.marker) === true &&
          document.querySelector(".editor-media") === null
        );
      }

      const image = document.querySelector<HTMLImageElement>(".editor-media img");
      if (image === null || !image.complete || image.naturalWidth !== 8) {
        return false;
      }
      const url = new URL(image.src);
      const path = url.searchParams.get("path");
      return (
        url.searchParams.get("session") === target.content.sessionId &&
        path !== null &&
        normalized(path).endsWith(target.content.pathSuffix) &&
        document.querySelector(".editor-media-notice") === null
      );
    };
    const nextFrame = (): Promise<void> =>
      new Promise((resolve) => requestAnimationFrame(() => resolve()));

    const started = performance.now();
    chip.click();
    for (;;) {
      await nextFrame();
      if (complete()) {
        // The next callback runs after the completed frame had a paint opportunity; recheck that it stayed true.
        await nextFrame();
        if (complete()) {
          return performance.now() - started;
        }
      }
    }
  }, expected);
}
