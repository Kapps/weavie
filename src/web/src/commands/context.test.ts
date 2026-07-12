import { beforeEach, describe, expect, it } from "vitest";
import { evaluateWhen, paneFocusContext, setContext } from "./context";

// The context is module-global; each test sets the keys it needs. Keys default to undefined (falsey).
describe("evaluateWhen", () => {
  beforeEach(() => {
    setContext("a", null);
    setContext("b", null);
    setContext("mode", null);
  });

  it("treats an empty or absent expression as always true", () => {
    expect(evaluateWhen(undefined)).toBe(true);
    expect(evaluateWhen("")).toBe(true);
    expect(evaluateWhen("   ")).toBe(true);
  });

  it("reads a bare key as a truthiness test", () => {
    setContext("a", true);
    expect(evaluateWhen("a")).toBe(true);
    setContext("a", false);
    expect(evaluateWhen("a")).toBe(false);
  });

  it("treats empty string, null, and unset as falsey", () => {
    setContext("a", "");
    expect(evaluateWhen("a")).toBe(false);
    setContext("a", null);
    expect(evaluateWhen("a")).toBe(false);
    expect(evaluateWhen("neverSet")).toBe(false);
  });

  it("negates with a leading !", () => {
    setContext("a", true);
    expect(evaluateWhen("!a")).toBe(false);
    setContext("a", false);
    expect(evaluateWhen("!a")).toBe(true);
  });

  it("compares with == and != against a quoted literal", () => {
    setContext("mode", "edit");
    expect(evaluateWhen("mode == 'edit'")).toBe(true);
    expect(evaluateWhen("mode != 'edit'")).toBe(false);
    expect(evaluateWhen('mode == "view"')).toBe(false);
    expect(evaluateWhen("mode != 'view'")).toBe(true);
  });

  it("an == against an unset key compares to the empty string", () => {
    expect(evaluateWhen("mode == ''")).toBe(true);
    expect(evaluateWhen("mode == 'edit'")).toBe(false);
  });

  it("ANDs clauses, failing if any clause fails", () => {
    setContext("a", true);
    setContext("b", true);
    expect(evaluateWhen("a && b")).toBe(true);
    setContext("b", false);
    expect(evaluateWhen("a && b")).toBe(false);
    expect(evaluateWhen("a && !b")).toBe(true);
  });

  it("combines a comparison with a truthiness clause", () => {
    setContext("mode", "edit");
    setContext("a", true);
    expect(evaluateWhen("a && mode == 'edit'")).toBe(true);
    setContext("a", false);
    expect(evaluateWhen("a && mode == 'edit'")).toBe(false);
  });
});

describe("paneFocusContext", () => {
  it("keeps the legacy agent pane kind while classifying a structured surface", () => {
    const input = elementInPane("terminal:claude", "structured-agent", false);

    expect(paneFocusContext(input)).toEqual({
      focusedPane: "terminal:claude",
      editorFocused: false,
      terminalFocused: false,
      agentFocused: true,
      agentComposerFocused: false,
    });
  });

  it("only enables composer commands from inside the structured composer", () => {
    const input = elementInPane("terminal:claude", "structured-agent", true);

    expect(paneFocusContext(input).agentComposerFocused).toBe(true);
  });

  it("classifies Claude in the same persisted pane as a terminal surface", () => {
    const pane = elementInPane("terminal:claude", "terminal", false);

    expect(paneFocusContext(pane)).toEqual({
      focusedPane: "terminal:claude",
      editorFocused: false,
      terminalFocused: true,
      agentFocused: false,
      agentComposerFocused: false,
    });
  });
});

function elementInPane(kind: string, surface: string, inComposer: boolean): Element {
  const pane = {
    getAttribute: (name: string) => (name === "data-kind" ? kind : surface),
  } as Element;
  return {
    closest: (selector: string) =>
      selector === "[data-agent-composer]" ? (inComposer ? pane : null) : pane,
  } as unknown as Element;
}
