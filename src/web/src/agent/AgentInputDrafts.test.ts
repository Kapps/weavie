import { describe, expect, it } from "vitest";
import type { AgentInputQuestion, ClientSession } from "../bridge";
import { agentInputDraft, clearAgentInputDraft } from "./AgentInputDrafts";

const questions: AgentInputQuestion[] = [
  {
    id: "secret",
    header: "Secret",
    question: "Token?",
    isSecret: true,
    options: [],
  },
];

describe("agent input drafts", () => {
  it("retains unresolved answers and drops only the resolved request", () => {
    const session = {} as ClientSession;
    const first = agentInputDraft(session, "first", questions);
    const second = agentInputDraft(session, "second", questions);
    first.setAnswers({ secret: ["sensitive"] });
    second.setAnswers({ secret: ["keep"] });

    expect(agentInputDraft(session, "first", questions).answers()).toEqual({
      secret: ["sensitive"],
    });
    clearAgentInputDraft(session, "first");

    expect(agentInputDraft(session, "first", questions).answers()).toEqual({ secret: [] });
    expect(agentInputDraft(session, "second", questions).answers()).toEqual({ secret: ["keep"] });
  });
});
