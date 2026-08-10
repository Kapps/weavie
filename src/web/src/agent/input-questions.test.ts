import { describe, expect, it } from "vitest";
import { inputQuestions } from "./input-questions";

describe("inputQuestions", () => {
  it("reads normalized provider-neutral questions", () => {
    const questions = [
      { id: "new", header: "New", question: "New?", isSecret: false, options: [] },
    ];
    expect(inputQuestions({ type: "input-requested", providerId: "codex", questions })).toBe(
      questions,
    );
  });

  it("returns no questions when the normalized field is absent", () => {
    expect(inputQuestions({ type: "input-requested", providerId: "codex" })).toEqual([]);
  });
});
