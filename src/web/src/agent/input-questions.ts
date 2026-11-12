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

export function isStructuredInput(
  message: AgentPaneUpdate | null | undefined,
): message is AgentPaneUpdate & { type: "input-requested" } {
  return message?.type === "input-requested" && message.itemType !== "url";
}

export function questionTitle(question: AgentInputQuestion): string {
  return question.header || question.question;
}

export function questionDescription(
  question: AgentInputQuestion,
  message: AgentPaneUpdate,
): string {
  return [questionTitle(question), message.summary, message.text].includes(question.question)
    ? ""
    : question.question;
}

/** Request context belongs to the card once, independently of its fields. */
export function inputRequestContext(message: AgentPaneUpdate): string[] {
  const titles = inputQuestions(message).map(questionTitle);
  return [...new Set([message.summary, message.text])].filter(
    (text): text is string =>
      typeof text === "string" && text.trim().length > 0 && !titles.includes(text),
  );
}
