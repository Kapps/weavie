import { expect, it, vi } from "vitest";
import type { ContextMenuItem } from "../chrome/ContextMenu";

const request = vi.hoisted(() => vi.fn());
vi.mock("../notify/notify", () => ({ notify: vi.fn() }));
vi.mock("./session-uri-owner", () => ({ sessionForUri: () => ({ feature: () => ({ request }) }) }));
vi.mock("./monaco-setup", () => ({
  monaco: {
    Range: class {
      constructor(
        public startLineNumber: number,
        public startColumn: number,
        public endLineNumber: number,
        public endColumn: number,
      ) {}
    },
    editor: { EditorOption: { readOnly: 1 } },
  },
}));

import type { monaco } from "./monaco-setup";
import { createSpellingActions } from "./spell-actions";

function fixture() {
  request.mockReset();
  request.mockResolvedValue(["the"]);
  const validity = new AbortController();
  let version = 1;
  const model = {
    uri: { toString: () => "session://owner/file.txt" },
    getVersionId: () => version,
    getValueInRange: () => "teh",
    isDisposed: () => false,
  };
  let mounted = model;
  const executeEdits = vi.fn();
  const pushUndoStop = vi.fn();
  const editor = {
    getModel: () => mounted,
    getOption: () => false,
    getTargetAtClientPoint: () => ({ position: { lineNumber: 1, column: 2 } }),
    executeEdits,
    pushUndoStop,
    focus: vi.fn(),
  };
  const actions = createSpellingActions(
    editor as unknown as monaco.editor.IStandaloneCodeEditor,
    () => ({ line: 1, offset: 0, word: "teh" }),
    () => validity.signal,
  );
  return {
    actions,
    validity,
    executeEdits,
    pushUndoStop,
    edit: () => {
      version++;
    },
    switchModel: () => {
      mounted = { ...model };
    },
  };
}

it("queries only when the menu loads and applies a chosen suggestion as one undoable edit", async () => {
  const { actions, executeEdits, pushUndoStop } = fixture();
  const menu = actions.menuAt(10, 20);
  expect(request).not.toHaveBeenCalled();
  const controller = new AbortController();
  const entries = await menu.loadEntries!(controller.signal);
  expect(request).toHaveBeenCalledWith("suggest", { word: "teh" }, expect.any(AbortSignal));
  controller.abort(); // Menus close before dispatching their selected command.
  actions.correct((entries[0] as ContextMenuItem).args);
  expect(executeEdits).toHaveBeenCalledWith("spelling", [
    {
      range: {
        startLineNumber: 1,
        startColumn: 1,
        endLineNumber: 1,
        endColumn: 4,
      },
      text: "the",
    },
  ]);
  expect(pushUndoStop).toHaveBeenCalledTimes(2);
});

it.each([
  "edit",
  "switchModel",
  "locale",
  "newMenu",
] as const)("rejects a correction after %s", async (change) => {
  const f = fixture();
  const entries = await f.actions.menuAt(10, 20).loadEntries!(new AbortController().signal);
  if (change === "locale") f.validity.abort();
  else if (change === "newMenu") f.actions.menuAt(10, 20);
  else f[change]();
  f.actions.correct((entries[0] as ContextMenuItem).args);
  expect(f.executeEdits).not.toHaveBeenCalled();
});

it.each([
  "dismiss",
  "locale",
])("cancels pending suggestions on %s and ignores late replies", async (change) => {
  const { actions, validity } = fixture();
  let finish: (value: string[]) => void = () => {};
  request.mockImplementation(
    () =>
      new Promise<string[]>((resolve) => {
        finish = resolve;
      }),
  );
  const controller = new AbortController();
  const pending = actions.menuAt(10, 20).loadEntries!(controller.signal);
  (change === "dismiss" ? controller : validity).abort();
  expect((request.mock.calls[0]![2] as AbortSignal).aborted).toBe(true);
  finish(["the"]);
  expect(await pending).toEqual([]);
});
