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

/** The modifier shared by keys that run 1, 2, 3… in option order ("Alt+"), so rows can show just the digit. */
export function sharedKeyPrefix(keys: readonly string[]): string | null {
  const prefix = keys[0]?.slice(0, -1);
  return prefix !== undefined && keys.every((key, index) => key === `${prefix}${index + 1}`)
    ? prefix
    : null;
}
