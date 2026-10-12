import { expect, it, vi } from "vitest";

vi.mock("./monaco-setup", () => ({
  monaco: {
    editor: {
      EditorOption: { lineHeight: 1 },
      ContentWidgetPositionPreference: { EXACT: 0 },
      PositionAffinity: { Right: 1 },
    },
  },
}));

import { addLineEndItem } from "./line-end-lane";
import type { monaco } from "./monaco-setup";

interface FakeElement {
  style: Record<string, string>;
  children: FakeElement[];
}

function element() {
  const children: FakeElement[] = [];
  return {
    className: "",
    style: {} as Record<string, string>,
    offsetWidth: 120,
    children,
    classList: { toggle: vi.fn() },
    appendChild: (child: FakeElement) => {
      children.push(child);
      return child;
    },
    replaceChildren: vi.fn(),
    remove: vi.fn(),
  };
}

// Measuring a line end makes Monaco flush a render, so a measurement taken inside a widget's render re-enters it.
it("measures an overflowing row's line end after the render, not inside it", async () => {
  vi.stubGlobal("document", { createElement: element });
  let widget: monaco.editor.IContentWidget | undefined;
  let rendering = false;
  const measuredWhileRendering: boolean[] = [];
  const editor = {
    getModel: () => ({ getLineCount: () => 10, getLineMaxColumn: () => 40 }),
    getOption: () => 20,
    getScrollLeft: () => 0,
    getLayoutInfo: () => ({ contentWidth: 300 }),
    getOffsetForColumn: () => {
      measuredWhileRendering.push(rendering);
      return 400;
    },
    addContentWidget: (added: monaco.editor.IContentWidget) => {
      widget = added;
    },
    onDidChangeModelContent: () => ({ dispose() {} }),
    onDidChangeModel: () => ({ dispose() {} }),
    onDidDispose: () => {},
  } as unknown as monaco.editor.ICodeEditor;
  const item = { dom: element(), order: 0, line: () => 3 };

  addLineEndItem(editor, item as unknown as Parameters<typeof addLineEndItem>[1]);
  await Promise.resolve();
  rendering = true;
  widget?.afterRender?.(null, { top: 0, left: 500 });
  rendering = false;
  await Promise.resolve();

  expect(measuredWhileRendering).toEqual([false]);
  const row = (widget?.getDomNode() as unknown as FakeElement).children[0];
  expect(row?.style.transform).toBe("translateX(-100px)");
});
