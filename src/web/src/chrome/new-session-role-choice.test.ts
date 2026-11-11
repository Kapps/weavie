import { describe, expect, it } from "vitest";
import type { RoleControls, RoleSuggestion } from "../bridge";
import type { AgentControlAxis } from "../messaging/protocol-types";
import {
  NewSessionRoleChoice,
  type RoleChoiceState,
  type RolePair,
  rolesReady,
} from "./new-session-role-choice";

const TARGET = { backendId: "local", sourceId: null, providerId: "codex" };
const option = (id: string, label: string) => ({ id, label, description: null, group: null });
const modelAxis = (value: string): AgentControlAxis => ({
  id: "model",
  label: "Model",
  description: null,
  category: "model",
  kind: "select",
  value,
  valueLabel: value === "" ? "No model" : value === "astra" ? "Astra" : "Sol",
  options: [option("astra", "Astra"), option("sol", "Sol")],
});
const effortAxis: AgentControlAxis = {
  id: "reasoning",
  label: "Effort",
  description: null,
  category: "thought_level",
  kind: "select",
  value: "",
  valueLabel: "Medium",
  options: [option("", "Default (Medium)"), option("low", "Low"), option("high", "High")],
};
/** What the agent reports: the models alone until one is chosen, then Astra's effort too. */
const controls = (model: string): RoleControls => ({
  axes: model === "astra" ? [modelAxis(model), effortAxis] : [modelAxis(model)],
  error: null,
});
const pair = (problem: string | null = null): RolePair => ({
  target: TARGET,
  workflow: "spec-driven",
  roles: [
    { role: "planner", problem },
    { role: "builder", problem: null },
  ],
});

const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

const harness = (answer: (model: string) => RoleControls | Promise<RoleControls> = controls) => {
  const states: RoleChoiceState[] = [];
  const asked: string[] = [];
  const suggestions = new Map<
    string,
    { resolve: (result: RoleSuggestion) => void; signal: AbortSignal }
  >();
  const choice = new NewSessionRoleChoice(
    async (_, model) => {
      asked.push(model);
      return answer(model);
    },
    (_, role, signal) => new Promise((resolve) => suggestions.set(role, { resolve, signal })),
    (state) => states.push(state),
  );
  const state = () => states.at(-1) as RoleChoiceState;
  return {
    choice,
    state,
    asked,
    suggestions,
    row: (role: string) => state().rows.find((row) => row.role === role),
    suggest: async (role: string, result: RoleSuggestion) => {
      suggestions.get(role)?.resolve(result);
      await flush();
    },
  };
};

const labels = (axes: readonly AgentControlAxis[] | undefined) =>
  axes?.map((axis) => `${axis.label}: ${axis.valueLabel}`);

describe("NewSessionRoleChoice", () => {
  it("is ready with no roles and waits for every role's model otherwise", async () => {
    const { choice, state, row } = harness();
    choice.show(null);
    expect(rolesReady(state())).toBe(true);

    choice.show(pair());
    expect(row("planner")?.axes).toBeUndefined();
    await flush();
    expect(labels(row("planner")?.axes)).toEqual(["Model: No model"]);
    choice.pick("planner", "model", "sol");
    choice.pick("builder", "model", "sol");
    // Not until the agent has said what the model offers: a start saves what is on screen.
    expect(row("builder")?.pending).toBe(true);
    expect(rolesReady(state())).toBe(false);
    await flush();
    expect(rolesReady(state())).toBe(true);
    expect(choice.capture()).toEqual({
      planner: { model: "sol", options: {} },
      builder: { model: "sol", options: {} },
    });
  });

  it("shows the options the chosen model offers and saves the ones picked with it", async () => {
    const { choice, row, asked } = harness();
    choice.show(pair());
    await flush();
    choice.pick("planner", "model", "astra");
    expect(labels(row("planner")?.axes)).toEqual(["Model: Astra"]);
    await flush();
    expect(labels(row("planner")?.axes)).toEqual(["Model: Astra", "Effort: Medium"]);

    choice.pick("planner", "reasoning", "high");
    expect(labels(row("planner")?.axes)).toEqual(["Model: Astra", "Effort: High"]);
    expect(choice.capture().planner).toEqual({ model: "astra", options: { reasoning: "high" } });
    choice.pick("planner", "reasoning", "");
    expect(choice.capture().planner).toEqual({ model: "astra", options: {} });

    // Another model has its own options, so the old ones go; the agent is asked once per model.
    choice.pick("planner", "reasoning", "high");
    choice.pick("planner", "model", "sol");
    await flush();
    expect(choice.capture().planner).toEqual({ model: "sol", options: {} });
    choice.pick("builder", "model", "astra");
    await flush();
    expect(asked).toEqual(["", "astra", "sol"]);
  });

  it("suggests only a role with nothing saved and never over a pick or a model not on offer", async () => {
    const { choice, row, suggestions, suggest } = harness();
    choice.show(pair('The saved model "nova" is no longer offered.'));
    await flush();
    expect([...suggestions.keys()]).toEqual(["builder"]);
    expect(row("builder")?.suggesting).toBe(true);

    choice.pick("builder", "model", "astra");
    expect(suggestions.get("builder")?.signal.aborted).toBe(true);
    await suggest("builder", { applies: true, model: "sol", error: null });
    expect(row("builder")?.model).toBe("astra");
    expect(row("planner")?.problem).toBe('The saved model "nova" is no longer offered.');
  });

  it("applies a confident suggestion on offer and reports a failed one by its row", async () => {
    const { choice, row, suggest } = harness();
    choice.show(pair());
    await flush();
    await suggest("planner", { applies: true, model: "gone", error: null });
    expect(row("planner")?.model).toBeNull();
    await suggest("builder", { applies: false, model: null, error: "the model is down" });
    expect(row("builder")?.problem).toBe("Model suggestion failed: the model is down");

    choice.show(null);
    choice.show(pair());
    await flush();
    await suggest("planner", { applies: true, model: "astra", error: null });
    expect(labels(row("planner")?.axes)).toEqual(["Model: Astra", "Effort: Medium"]);
    expect(row("planner")?.suggesting).toBe(false);
  });

  it("reports a failed model list, asks nothing, and lists again when shown again", async () => {
    let failing = true;
    const { choice, state, row, suggestions } = harness((model) =>
      failing ? { axes: [], error: "probe failed" } : controls(model),
    );
    choice.show(pair());
    await flush();
    expect(state().error).toBe("Couldn't list models: probe failed");
    expect(suggestions.size).toBe(0);
    expect(rolesReady(state())).toBe(false);

    failing = false;
    choice.show(pair());
    expect(state().error).toBeNull();
    await flush();
    expect(labels(row("planner")?.axes)).toEqual(["Model: No model"]);
    expect([...suggestions.keys()]).toEqual(["planner", "builder"]);
  });

  it("keeps the picked model, says why its options can't be asked for, and asks again next time", async () => {
    let failing = true;
    const { choice, row, asked } = harness((model) => {
      if (model !== "astra" || !failing) return controls(model);
      throw new Error("agent refused");
    });
    choice.show(pair());
    await flush();
    choice.pick("planner", "model", "astra");
    await flush();
    expect(row("planner")).toMatchObject({
      pending: false,
      problem: "Couldn't get the options: agent refused",
    });
    expect(choice.capture().planner).toEqual({ model: "astra", options: {} });

    failing = false;
    choice.pick("builder", "model", "astra");
    await flush();
    expect(labels(row("builder")?.axes)).toEqual(["Model: Astra", "Effort: Medium"]);
    expect(asked).toEqual(["", "astra", "astra"]);
  });

  it("ignores a model's options that land after another model was picked", async () => {
    const late: ((result: RoleControls) => void)[] = [];
    const { choice, row } = harness((model) =>
      model === "astra" ? new Promise((resolve) => late.push(resolve)) : controls(model),
    );
    choice.show(pair());
    await flush();
    choice.pick("planner", "model", "astra");
    choice.pick("planner", "model", "sol");
    await flush();
    late[0]?.(controls("astra"));
    await flush();
    expect(labels(row("planner")?.axes)).toEqual(["Model: Sol"]);
    expect(row("planner")?.pending).toBe(false);
  });

  it("discards a suggestion still on its way when a start captures the rows", async () => {
    const { choice, state, row, suggestions, suggest } = harness();
    choice.show(pair());
    await flush();
    expect(choice.capture()).toEqual({});
    expect(suggestions.get("planner")?.signal.aborted).toBe(true);
    await suggest("planner", { applies: true, model: "sol", error: null });
    expect(row("planner")?.model).toBeNull();
    expect(state().rows.every((candidate) => !candidate.suggesting)).toBe(true);
  });
});
