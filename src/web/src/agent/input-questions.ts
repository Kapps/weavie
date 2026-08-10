import type { AgentInputQuestion, AgentPaneUpdate } from "../bridge";

export function inputQuestions(message: AgentPaneUpdate): AgentInputQuestion[] {
  return message.questions ?? [];
}
