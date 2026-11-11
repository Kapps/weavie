import { describe, expect, it } from "vitest";
import type { RoleModels, RoleSuggestion } from "../bridge";
import {
  NewSessionRoleChoice,
  type RoleChoiceState,
  type RolePair,
  rolesReady,
} from "./new-session-role-choice";

const TARGET = { backendId: "local", sourceId: null, providerId: "codex" };
const MODELS: RoleModels = {
  models: [
    { id: "astra", label: "Astra", description: null, group: null },
    { id: "sol", label: "Sol", description: null, group: null },
  ],
  error: null,
};
const pair = (problem: string | null = null): RolePair => ({
  target: TARGET,
  workflow: "spec-driven",
  roles: [
    { role: "planner", problem },
    { role: "builder", problem: null },
  ],
});

const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

const harness = () => {
  const states: RoleChoiceState[] = [];
  const models: ((result: RoleModels) => void)[] = [];
  const suggestions = new Map<
    string,
    { resolve: (result: RoleSuggestion) => void; signal: AbortSignal }
  >();
  const choice = new NewSessionRoleChoice(
    () => new Promise((resolve) => models.push(resolve)),
    (_, role, signal) => new Promise((resolve) => suggestions.set(role, { resolve, signal })),
    (state) => states.push(state),
  );
  return {
    choice,
    state: () => states.at(-1) as RoleChoiceState,
    models: async (result: RoleModels) => {
      models.at(-1)?.(result);
      await flush();
    },
    suggestions,
    suggest: async (role: string, result: RoleSuggestion) => {
      suggestions.get(role)?.resolve(result);
      await flush();
    },
  };
};

const model = (state: RoleChoiceState, role: string) =>
  state.rows.find((row) => row.role === role)?.model;

describe("NewSessionRoleChoice", () => {
  it("is ready with no roles and waits for every role's model otherwise", async () => {
    const { choice, state, models } = harness();
    choice.show(null);
    expect(rolesReady(state())).toBe(true);

    choice.show(pair());
    expect(state().models).toBeNull();
    expect(rolesReady(state())).toBe(false);
    await models(MODELS);
    choice.pick("planner", "astra");
    expect(rolesReady(state())).toBe(false);
    choice.pick("builder", "sol");
    expect(rolesReady(state())).toBe(true);
    expect(choice.capture()).toEqual({ planner: "astra", builder: "sol" });
  });

  it("suggests only a role with nothing saved and never over a pick or a model not on offer", async () => {
    const { choice, state, models, suggestions, suggest } = harness();
    choice.show(pair('The saved model "nova" is no longer offered.'));
    await models(MODELS);
    expect([...suggestions.keys()]).toEqual(["builder"]);
    expect(state().rows.find((row) => row.role === "builder")?.suggesting).toBe(true);

    choice.pick("builder", "astra");
    expect(suggestions.get("builder")?.signal.aborted).toBe(true);
    await suggest("builder", { applies: true, model: "sol", error: null });
    expect(model(state(), "builder")).toBe("astra");
    expect(state().rows[0]?.problem).toBe('The saved model "nova" is no longer offered.');
  });

  it("applies a confident suggestion on offer and reports a failed one by its row", async () => {
    const { choice, state, models, suggest } = harness();
    choice.show({ ...pair(), roles: [{ role: "planner", problem: null }] });
    await models(MODELS);
    await suggest("planner", { applies: true, model: "gone", error: null });
    expect(model(state(), "planner")).toBeNull();

    choice.show(null);
    choice.show({ ...pair(), roles: [{ role: "planner", problem: null }] });
    await models(MODELS);
    await suggest("planner", { applies: false, model: null, error: "the model is down" });
    expect(state().rows[0]?.problem).toBe("Model suggestion failed: the model is down");

    choice.show(null);
    choice.show({ ...pair(), roles: [{ role: "planner", problem: null }] });
    await models(MODELS);
    await suggest("planner", { applies: true, model: "sol", error: null });
    expect(model(state(), "planner")).toBe("sol");
  });

  it("reports a failed model list and asks nothing", async () => {
    const { choice, state, models, suggestions } = harness();
    choice.show(pair());
    await models({ models: [], error: "probe failed" });
    expect(state().error).toBe("Couldn't list models: probe failed");
    expect(suggestions.size).toBe(0);
    expect(rolesReady(state())).toBe(false);
  });

  it("discards a suggestion still on its way when a start captures the rows", async () => {
    const { choice, state, models, suggestions, suggest } = harness();
    choice.show(pair());
    await models(MODELS);
    expect(choice.capture()).toEqual({});
    expect(suggestions.get("planner")?.signal.aborted).toBe(true);
    await suggest("planner", { applies: true, model: "sol", error: null });
    expect(model(state(), "planner")).toBeNull();
    expect(state().rows.every((row) => !row.suggesting)).toBe(true);
  });
});
