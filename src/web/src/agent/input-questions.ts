import type { AgentInputQuestion, AgentPaneUpdate } from "../bridge";

export function inputQuestions(message: AgentPaneUpdate): AgentInputQuestion[] {
  return message.questions ?? legacyQuestions(message.payload);
}

function legacyQuestions(payload: unknown): AgentInputQuestion[] {
  if (!isRecord(payload) || !isRecord(payload.params) || !Array.isArray(payload.params.questions)) {
    return [];
  }
  return payload.params.questions.flatMap((value): AgentInputQuestion[] => {
    if (!isRecord(value) || typeof value.id !== "string" || typeof value.question !== "string") {
      return [];
    }
    return [
      {
        id: value.id,
        header: typeof value.header === "string" ? value.header : "",
        question: value.question,
        isSecret: value.isSecret === true,
        options: Array.isArray(value.options) ? value.options.flatMap(readLegacyOption) : [],
      },
    ];
  });
}

function readLegacyOption(value: unknown): AgentInputQuestion["options"] {
  if (!isRecord(value) || typeof value.label !== "string") {
    return [];
  }
  return [
    {
      label: value.label,
      description: typeof value.description === "string" ? value.description : "",
    },
  ];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
