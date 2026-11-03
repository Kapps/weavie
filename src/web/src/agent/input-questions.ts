import type { AgentInputQuestion, AgentPaneUpdate } from "../bridge";

export function inputQuestions(message: AgentPaneUpdate): AgentInputQuestion[] {
  return message.questions ?? [];
}

/** The request's question when it is exactly one pick-one choice, which renders as answer-on-click rows. */
export function singleChoiceQuestion(message: AgentPaneUpdate): AgentInputQuestion | null {
  const [question, ...rest] = inputQuestions(message);
  return question !== undefined &&
    rest.length === 0 &&
    question.kind !== "array" &&
    question.options.length > 0
    ? question
    : null;
}
