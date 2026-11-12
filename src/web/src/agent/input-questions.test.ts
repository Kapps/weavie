import { describe, expect, it } from "vitest";
import {
  inputQuestions,
  inputRequestContext,
  questionDescription,
  questionTitle,
} from "./input-questions";

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

describe("question presentation", () => {
  const question = {
    id: "answer",
    header: "Which view?",
    question: "",
    allowsOther: true,
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
  };
  const message = {
    type: "input-requested",
    providerId: "acp",
    summary: "Answers expire when the turn ends",
    questions: [question],
  };

  it("keeps the field title visible and request context separate with no description", () => {
    expect(questionTitle(question)).toBe("Which view?");
    expect(questionDescription(question, message)).toBe("");
    expect(inputRequestContext(message)).toEqual([message.summary]);
  });

  it("shows distinct field guidance but not an exact repetition of title or request context", () => {
    expect(questionDescription({ ...question, question: "Choose the scope" }, message)).toBe(
      "Choose the scope",
    );
    expect(questionDescription({ ...question, question: question.header }, message)).toBe("");
    expect(questionDescription({ ...question, question: message.summary }, message)).toBe("");
    expect(inputRequestContext({ ...message, text: message.summary })).toEqual([message.summary]);
    expect(inputRequestContext({ ...message, summary: question.header })).toEqual([]);
  });
});
