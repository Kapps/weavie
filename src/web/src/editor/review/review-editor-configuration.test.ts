import { beforeEach, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({
  resource: {} as Record<string, unknown>,
  configured: {} as Record<string, unknown>,
  review: {} as Record<string, unknown>,
  getValue: vi.fn(),
}));
vi.mock("@codingame/monaco-vscode-api", () => ({
  StandaloneServices: { get: () => ({ getValue: state.getValue }) },
}));
vi.mock("../monaco-setup", () => ({ configuredEditorOptions: () => state.configured }));
vi.mock("./review-editor-options", () => ({ reviewEditorOptions: () => state.review }));

import type { monaco } from "../monaco-setup";
import { reviewEditorConfiguration } from "./review-editor-configuration";

beforeEach(() => {
  state.resource = { glyphMargin: true, padding: { top: 99 }, fontSize: 12, readOnly: true };
  state.configured = { fontSize: 16 };
  state.review = { padding: { top: 6 } };
  state.getValue.mockReset().mockImplementation(() => state.resource);
});

it("captures authoritative resource defaults with live-editor override precedence", () => {
  const model = { uri: { path: "/a.txt" } } as monaco.editor.ITextModel;
  const captured = reviewEditorConfiguration(model, { readOnly: false });
  expect(state.getValue).toHaveBeenCalledWith(model.uri, "editor");
  expect(captured).toEqual({
    glyphMargin: true,
    padding: { top: 6 },
    fontSize: 16,
    readOnly: false,
  });
  state.resource.padding = { top: 123 };
  expect(reviewEditorConfiguration(model, { readOnly: false })).toEqual(captured);
  state.resource.glyphMargin = false;
  expect(reviewEditorConfiguration(model, { readOnly: false })).not.toEqual(captured);
});

it("does not let subsequent nested option mutations rewrite the captured configuration", () => {
  const model = { uri: {} } as monaco.editor.ITextModel;
  const captured = reviewEditorConfiguration(model, {});
  (state.review.padding as { top: number }).top = 25;
  expect(captured.padding).toEqual({ top: 6 });
  expect(reviewEditorConfiguration(model, {}).padding).toEqual({ top: 25 });
});
