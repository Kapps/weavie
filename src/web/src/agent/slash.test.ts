import { describe, expect, it } from "vitest";
import type { AgentControlState, AgentSlashEntry } from "../bridge";
import {
  classifyAgentDraft,
  filterSlash,
  providerCommandForDraft,
  slashQuery,
  weavieCommandForDraft,
  weavieCommandInput,
} from "./slash";

const entry = (name: string): Extract<AgentSlashEntry, { kind: "providerCommand" }> => ({
  id: name,
  name,
  description: name,
  kind: "providerCommand",
  commandId: null,
  inputHint: null,
  inputName: null,
});

const clear: AgentSlashEntry = {
  id: "weavie:clear",
  name: "clear",
  description: "Clear",
  kind: "weavieCommand",
  commandId: "weavie.agent.clearConversation",
  inputHint: null,
  inputName: null,
};

const btw: AgentSlashEntry = {
  id: "weavie:btw",
  name: "btw",
  description: "Ask aside",
  kind: "weavieCommand",
  commandId: "weavie.agent.askAside",
  inputHint: "question",
  inputName: "question",
};

describe("weavieCommandForDraft", () => {
  it("matches only an exact client-owned slash action", () => {
    expect(weavieCommandForDraft([clear], "/CLEAR")?.name).toBe("clear");
    expect(weavieCommandForDraft([clear], "/clear now")).toBeNull();
  });

  it("requires and extracts free-form input for an argument-taking action", () => {
    expect(weavieCommandForDraft([btw], "/btw")).toBe(btw);
    expect(weavieCommandInput(btw, "/btw")).toBeNull();
    expect(weavieCommandForDraft([btw], "/BTW   why this design?")).toBe(btw);
    expect(weavieCommandInput(btw, "/btw   why this design?")).toBe("why this design?");
  });
});

describe("providerCommandForDraft", () => {
  const entries = [entry("compact"), entry("review")];

  it("matches an exact advertised command with optional arguments", () => {
    expect(providerCommandForDraft(entries, "/COMPACT")?.name).toBe("compact");
    expect(providerCommandForDraft(entries, "  /compact  ")?.name).toBe("compact");
    expect(providerCommandForDraft(entries, "/review focus on tests")?.name).toBe("review");
  });

  it("does not promote unknown or prefix text into a command", () => {
    expect(providerCommandForDraft(entries, "/compactly")).toBeNull();
    expect(providerCommandForDraft(entries, "explain /compact")).toBeNull();
  });
});

describe("slashQuery", () => {
  it("returns the query while the draft is a whitespace-free slash token", () => {
    expect(slashQuery("/")).toBe("");
    expect(slashQuery("/mod")).toBe("mod");
  });

  it("is inactive once the draft is a prompt or not a slash command", () => {
    expect(slashQuery("")).toBeNull();
    expect(slashQuery("hello")).toBeNull();
    expect(slashQuery("/model do the thing")).toBeNull();
    expect(slashQuery(" /model")).toBeNull();
  });
});

describe("filterSlash", () => {
  const entries = [entry("model"), entry("approvals"), entry("sandbox"), entry("review-pr")];

  it("returns all entries for an empty query", () => {
    expect(filterSlash(entries, "").map((match) => match.name)).toEqual([
      "model",
      "approvals",
      "sandbox",
      "review-pr",
    ]);
  });

  it("filters by case-insensitive substring", () => {
    expect(filterSlash(entries, "AP").map((match) => match.name)).toEqual(["approvals"]);
    expect(filterSlash(entries, "an").map((match) => match.name)).toEqual(["sandbox"]);
    expect(filterSlash(entries, "review").map((match) => match.name)).toEqual(["review-pr"]);
  });

  it("caps the list at eight entries", () => {
    const many = Array.from({ length: 20 }, (_, index) => entry(`skill-${index}`));
    expect(filterSlash(many, "skill")).toHaveLength(8);
  });
});

describe("classifyAgentDraft", () => {
  const controls = (ready: boolean, slash: AgentSlashEntry[]): AgentControlState => ({
    ready,
    axes: [],
    slash,
  });

  it.each([
    { slash: [] },
    { slash: [clear] },
  ])("holds aside input while the provider catalog is incomplete: $slash", ({ slash }) => {
    expect(classifyAgentDraft(controls(false, slash), "/btw rich")).toEqual({ kind: "loading" });
  });

  it("resolves the retained aside draft when its command becomes available", () => {
    const draft = "  /BTW   rich details\nwith another line";
    expect(classifyAgentDraft(controls(false, [clear]), draft)).toEqual({ kind: "loading" });
    const action = classifyAgentDraft(controls(true, [clear, btw]), draft);
    expect(action).toEqual({ kind: "command", entry: btw });
    expect(weavieCommandInput(btw, draft)).toBe("rich details\nwith another line");
  });

  it("allows local actions and ordinary prompts before the provider is ready", () => {
    const state = controls(false, [clear]);
    expect(classifyAgentDraft(state, "/clear")).toEqual({ kind: "command", entry: clear });
    expect(classifyAgentDraft(state, "explain this function")).toEqual({ kind: "prompt" });
    expect(classifyAgentDraft(state, "")).toEqual({ kind: "prompt" });
  });

  it("blocks a stale provider command until the provider is ready", () => {
    const compact = entry("compact");
    expect(classifyAgentDraft(controls(false, [clear, compact]), "/compact")).toEqual({
      kind: "loading",
    });
    expect(classifyAgentDraft(controls(true, [clear, compact]), "/compact")).toEqual({
      kind: "command",
      entry: compact,
    });
  });

  it.each([
    "/unknown explain this",
    "/usr/local/bin/tool",
    "/btw rich",
    "/clear now",
  ])("keeps unsupported slash input literal after readiness: %s", (draft) => {
    expect(classifyAgentDraft(controls(true, [clear]), draft)).toEqual({ kind: "prompt" });
  });
});
