import { NavigationCommandRevealType } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/coreCommands";
import type { IMouseTargetOutsideEditor } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorBrowser";
import type { EditorMouseEvent } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorDom";
import { describe, expect, it, vi } from "vitest";
import type { monaco } from "../monaco-setup";
import { observeReviewMouseGesture } from "./review-mouse-handoff";

vi.mock("@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorDom", () => ({
  EditorMouseEvent: class {},
}));
vi.mock("@codingame/monaco-vscode-api/vscode/vs/editor/browser/coreCommands", () => ({
  NavigationCommandRevealType: { None: Symbol("no reveal") },
}));

function fixture() {
  let active = false;
  const listeners = new Set<() => void>();
  const operation = {
    _mouseState: { startedOnLineNumbers: false },
    _leftRightDragScrolling: Object.create({ start: vi.fn() }) as {
      start(position: IMouseTargetOutsideEditor, event: EditorMouseEvent): void;
    },
    _dispatchMouse: vi.fn(),
    isActive: () => active,
    _stop: () => {
      active = false;
    },
  };
  const original = operation._stop;
  const editor = {
    _modelData: { view: { _pointerHandler: { handler: { _mouseDownOperation: operation } } } },
    onMouseDown: (listener: () => void) => {
      listeners.add(listener);
      return { dispose: () => listeners.delete(listener) };
    },
  } as unknown as monaco.editor.IStandaloneCodeEditor;
  return {
    editor,
    operation,
    original,
    listeners,
    down: () => {
      active = true;
      for (const listener of listeners) listener();
    },
  };
}

describe("exact review mouse monitor ownership", () => {
  it("dispatches gutter selection once after scheduling, without waiting for a frame", () => {
    const f = fixture();
    const drag = f.operation._leftRightDragScrolling;
    const start = drag.start;
    const observer = observeReviewMouseGesture(f.editor, vi.fn());
    const position = {} as IMouseTargetOutsideEditor;
    const event = {} as EditorMouseEvent;
    drag.start(position, event);
    expect(f.operation._dispatchMouse).not.toHaveBeenCalled();
    f.operation._mouseState.startedOnLineNumbers = true;
    drag.start(position, event);
    expect(f.operation._dispatchMouse).toHaveBeenCalledExactlyOnceWith(
      position,
      true,
      NavigationCommandRevealType.None,
    );
    expect(vi.mocked(start).mock.invocationCallOrder[1]).toBeLessThan(
      f.operation._dispatchMouse.mock.invocationCallOrder[0]!,
    );
    observer.dispose();
    expect(drag.start).toBe(start);
    expect(Object.hasOwn(drag, "start")).toBe(false);
  });

  it("preserves another hook owner while restoring the remaining hook", () => {
    const f = fixture();
    const observer = observeReviewMouseGesture(f.editor, vi.fn());
    const captured = f.operation._leftRightDragScrolling.start;
    const replacement = vi.fn((...args: Parameters<typeof captured>) => captured(...args));
    f.operation._leftRightDragScrolling.start = replacement;
    expect(() => observer.dispose()).toThrow("was replaced");
    expect(f.operation._leftRightDragScrolling.start).toBe(replacement);
    expect(f.operation._stop).toBe(f.original);
    expect(f.listeners.size).toBe(0);
    f.operation._mouseState.startedOnLineNumbers = true;
    replacement({} as IMouseTargetOutsideEditor, {} as EditorMouseEvent);
    expect(f.operation._dispatchMouse).not.toHaveBeenCalled();
    observer.dispose();
  });
  it("follows the actual stop boundary and has idempotent teardown", () => {
    const f = fixture();
    const changed = vi.fn();
    const observer = observeReviewMouseGesture(f.editor, changed);
    f.down();
    f.operation._stop();
    expect(changed.mock.calls).toEqual([[false], [true], [false]]);
    observer.dispose();
    observer.dispose();
    expect(f.operation._stop).toBe(f.original);
    expect(f.listeners.size).toBe(0);
    expect(Object.hasOwn(f.operation._leftRightDragScrolling, "start")).toBe(false);
    f.down();
    expect(changed).toHaveBeenCalledTimes(3);
  });

  it("rejects a second owner before mutating the first observer", () => {
    const f = fixture();
    const changed = vi.fn();
    const first = observeReviewMouseGesture(f.editor, changed);
    const installed = f.operation._stop;
    expect(() => observeReviewMouseGesture(f.editor, vi.fn())).toThrow("already has an owner");
    expect(f.operation._stop).toBe(installed);
    expect(f.listeners.size).toBe(1);
    f.down();
    first.dispose();
    const second = observeReviewMouseGesture(f.editor, vi.fn());
    second.dispose();
    expect(f.operation._stop).toBe(f.original);
  });

  it("rolls back the method and subscription when initial publication throws", () => {
    const f = fixture();
    expect(() =>
      observeReviewMouseGesture(f.editor, () => {
        throw new Error("observer failed");
      }),
    ).toThrow("observer failed");
    expect(f.operation._stop).toBe(f.original);
    expect(f.listeners.size).toBe(0);
    observeReviewMouseGesture(f.editor, vi.fn()).dispose();
    expect(f.operation._stop).toBe(f.original);
  });
});
