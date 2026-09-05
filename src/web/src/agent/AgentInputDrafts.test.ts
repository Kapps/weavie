import { describe, expect, it, vi } from "vitest";
import type { AgentInputQuestion, ClientSession } from "../bridge";
import { agentInputDraft, clearAgentInputDraft } from "./AgentInputDrafts";

vi.mock("../bridge", () => ({ registerSessionFeature: () => () => {} }));

const questions: AgentInputQuestion[] = [
  {
    id: "secret",
    header: "Secret",
    question: "Token?",
    allowsOther: false,
    kind: "string",
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

const booleanQuestion: AgentInputQuestion = {
  ...questions[0]!,
  id: "enabled",
  kind: "boolean",
  required: true,
};

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

  it("submits an untouched required boolean as false", () => {
    const session = {} as ClientSession;

    expect(agentInputDraft(session, "boolean", [booleanQuestion]).answers()).toEqual({
      enabled: ["false"],
    });
  });

  it("returns a transient draft after its session closes", () => {
    const session = { closed: true } as ClientSession;
    expect(agentInputDraft(session, "closed", questions).answers()).toEqual({ secret: [] });
  });
});
