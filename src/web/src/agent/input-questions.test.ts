import { describe, expect, it } from "vitest";
import { inputQuestions, sharedKeyPrefix } from "./input-questions";

describe("inputQuestions", () => {
  it("reads normalized provider-neutral questions", () => {
    const questions = [
      {
        id: "new",
        header: "New",
        question: "New?",
        allowsOther: false,
        kind: "string" as const,
        required: false,
        format: null,
        initialValues: [],
        minimum: null,
        maximum: null,
        minimumLength: null,
        maximumLength: null,
        pattern: null,
        options: [],
      },
    ];
    expect(inputQuestions({ type: "input-requested", providerId: "acp", questions })).toBe(
      questions,
    );
  });

  it("returns no questions when the normalized field is absent", () => {
    expect(inputQuestions({ type: "input-requested", providerId: "acp" })).toEqual([]);
  });
});

describe("sharedKeyPrefix", () => {
  it("finds the modifier of keys numbered in option order", () => {
    expect(sharedKeyPrefix(["Alt+1", "Alt+2", "Alt+3"])).toBe("Alt+");
  });

  it("refuses rebound or missing keys, which rows must show in full", () => {
    expect(sharedKeyPrefix(["Alt+1", "Ctrl+Q"])).toBeNull();
    expect(sharedKeyPrefix(["Alt+1", "Alt+3"])).toBeNull();
    expect(sharedKeyPrefix([])).toBeNull();
  });
});
