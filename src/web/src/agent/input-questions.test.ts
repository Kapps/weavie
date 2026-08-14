import { describe, expect, it } from "vitest";
import { inputQuestions } from "./input-questions";

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
