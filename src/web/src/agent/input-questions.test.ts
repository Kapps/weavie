import { describe, expect, it } from "vitest";
import { inputQuestions } from "./input-questions";

describe("inputQuestions", () => {
  it("prefers the normalized provider-neutral questions", () => {
    const questions = [
      { id: "new", header: "New", question: "New?", isSecret: false, options: [] },
    ];
    expect(
      inputQuestions({ type: "input-requested", providerId: "codex", questions, payload: {} }),
    ).toBe(questions);
  });

  it("reads the protocol-1 Codex payload from an older remote backend", () => {
    expect(
      inputQuestions({
        type: "input-requested",
        providerId: "codex",
        payload: {
          params: {
            questions: [
              {
                id: "mode",
                header: "Mode",
                question: "Which mode?",
                options: [{ label: "Safe", description: "Use safe mode." }],
              },
            ],
          },
        },
      }),
    ).toEqual([
      {
        id: "mode",
        header: "Mode",
        question: "Which mode?",
        isSecret: false,
        options: [{ label: "Safe", description: "Use safe mode." }],
      },
    ]);
  });
});
