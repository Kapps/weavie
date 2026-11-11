import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WorkflowStartCatalog, WorkflowSuggestion } from "../bridge";
import { DRAFT_SETTLE_IDLE_MS } from "./new-session-draft-settle";
import {
  NewSessionWorkflowChoice,
  type WorkflowChoiceState,
  type WorkflowTarget,
} from "./new-session-workflow-choice";

const DRAFT = "the WebM video in the review pane fails to load whenever the diff is reopened twice";
const SHORT = "WebM fails to load";
const LOCAL: WorkflowTarget = { backendId: "local", sourceId: null, providerId: "codex" };
const REMOTE: WorkflowTarget = { backendId: "remote", sourceId: null, providerId: "codex" };

const catalog = (overrides: Partial<WorkflowStartCatalog>): WorkflowStartCatalog => ({
  workflows: [
    { id: "broken", description: "", error: "run needs exactly one of prompt or skill", roles: [] },
    { id: "quick-fix", description: "A small change.", error: null, roles: [] },
    { id: "spec-driven", description: "Requirements first.", error: null, roles: [] },
  ],
  default: null,
  defaultProblem: null,
  ...overrides,
});

const applies = (workflow: string | null): WorkflowSuggestion => ({
  applies: true,
  workflow,
  error: null,
});

interface Harness {
  choice: NewSessionWorkflowChoice;
  state: () => WorkflowChoiceState;
  suggestions: { target: WorkflowTarget; prompt: string; signal: AbortSignal }[];
  answer: (result: WorkflowSuggestion) => Promise<void>;
  catalogs: Map<string, WorkflowStartCatalog>;
}

const harness = (): Harness => {
  const states: WorkflowChoiceState[] = [];
  const suggestions: Harness["suggestions"] = [];
  const answers: ((result: WorkflowSuggestion) => void)[] = [];
  const catalogs = new Map([
    ["local", catalog({})],
    [
      "remote",
      catalog({ workflows: [{ id: "remote-only", description: "", error: null, roles: [] }] }),
    ],
  ]);
  const choice = new NewSessionWorkflowChoice(
    async (target) => catalogs.get(target.backendId) ?? Promise.reject(new Error("offline")),
    (target, prompt, signal) => {
      suggestions.push({ target, prompt, signal });
      return new Promise((resolve) => answers.push(resolve));
    },
    (state) => states.push(state),
  );
  return {
    choice,
    state: () => states.at(-1) as WorkflowChoiceState,
    suggestions,
    catalogs,
    answer: async (result) => {
      answers.at(-1)?.(result);
      await vi.advanceTimersByTimeAsync(0);
    },
  };
};

const settle = (): Promise<unknown> => vi.advanceTimersByTimeAsync(DRAFT_SETTLE_IDLE_MS);

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("NewSessionWorkflowChoice", () => {
  it("starts on the configured default and reports why a default can't be used", async () => {
    const { choice, state, catalogs } = harness();
    catalogs.set("local", catalog({ default: "quick-fix" }));
    choice.retarget(LOCAL);
    expect(state().status).toBe("loading");
    await vi.advanceTimersByTimeAsync(0);
    expect(state()).toMatchObject({ workflow: "quick-fix", problem: null, status: "ready" });

    catalogs.set("remote", catalog({ defaultProblem: 'The default workflow "x" is invalid.' }));
    choice.retarget(REMOTE);
    await vi.advanceTimersByTimeAsync(0);
    expect(state()).toMatchObject({
      workflow: null,
      problem: 'The default workflow "x" is invalid.',
    });

    choice.retarget({ backendId: "gone", sourceId: null, providerId: "codex" });
    await vi.advanceTimersByTimeAsync(0);
    expect(state()).toMatchObject({ options: [], problem: "Couldn't list workflows: offline" });
  });

  it("asks once for a settled draft worth judging, and never again as it changes", async () => {
    const { choice, state, suggestions, answer } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);

    choice.draft(SHORT);
    await settle();
    choice.flush();
    expect(suggestions).toEqual([]);

    choice.draft(DRAFT);
    await vi.advanceTimersByTimeAsync(DRAFT_SETTLE_IDLE_MS - 1);
    expect(suggestions).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(suggestions).toMatchObject([{ target: LOCAL, prompt: DRAFT }]);
    expect(state().status).toBe("suggesting");

    await answer(applies("quick-fix"));
    expect(state()).toMatchObject({ workflow: "quick-fix", status: "ready" });

    choice.draft(`${DRAFT} after a reconnect`);
    await settle();
    choice.flush();
    expect(suggestions).toHaveLength(1);
  });

  it("asks at once when focus leaves a draft worth judging", async () => {
    const { choice, suggestions } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);

    choice.draft(DRAFT);
    choice.flush();

    expect(suggestions).toHaveLength(1);
  });

  it("leaves the value alone below the threshold and shows why a suggestion failed", async () => {
    const { choice, state, answer } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.flush();

    await answer({
      applies: false,
      workflow: null,
      error: "The decision exceeded its time budget.",
    });

    expect(state()).toMatchObject({
      workflow: null,
      status: "ready",
      problem: "Workflow suggestion failed: The decision exceeded its time budget.",
    });
  });

  it("ignores a suggestion the catalog doesn't offer as startable", async () => {
    const { choice, state, answer } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.flush();

    await answer(applies("broken"));

    expect(state().workflow).toBeNull();
  });

  it("never lets a suggestion land over the user's pick", async () => {
    const { choice, state, suggestions, answer } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.flush();

    choice.pick("spec-driven");
    expect(suggestions[0]?.signal.aborted).toBe(true);
    await answer(applies("quick-fix"));
    expect(state()).toMatchObject({ workflow: "spec-driven", status: "ready" });

    choice.pick(null);
    choice.draft(`${DRAFT} again`);
    await settle();
    expect(suggestions).toHaveLength(1);
    expect(state().workflow).toBeNull();
  });

  it("asks and applies nothing while the user is on the field", async () => {
    const { choice, state, suggestions, answer } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.claim();
    await settle();
    expect(suggestions).toEqual([]);

    choice.release();
    await settle();
    expect(suggestions).toHaveLength(1);
    choice.claim();
    await answer(applies("quick-fix"));
    expect(state()).toMatchObject({ workflow: null, status: "ready" });
  });

  it("starts with the value shown and discards a suggestion still on its way", async () => {
    const { choice, state, answer } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.flush();

    expect(choice.capture()).toBeNull();
    await answer(applies("quick-fix"));

    expect(state()).toMatchObject({ workflow: null, status: "ready" });
  });

  it("re-reads a moved target without asking again, keeping only a value it still offers", async () => {
    const { choice, state, suggestions, answer, catalogs } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.flush();
    await answer(applies("quick-fix"));

    choice.retarget(REMOTE);
    await vi.advanceTimersByTimeAsync(0);
    expect(state().workflow).toBeNull();

    choice.pick("remote-only");
    catalogs.set(
      "local",
      catalog({ workflows: [{ id: "remote-only", description: "", error: null, roles: [] }] }),
    );
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    expect(state().workflow).toBe("remote-only");
    choice.flush();
    expect(suggestions).toHaveLength(1);
  });

  it("waits out an unreachable host without asking again or losing the applied suggestion", async () => {
    const { choice, state, suggestions, answer } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.flush();
    await answer(applies("quick-fix"));

    choice.retarget(null);
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);

    expect(suggestions).toHaveLength(1);
    expect(state()).toMatchObject({ workflow: "quick-fix", status: "ready" });
  });

  it("asks about a draft edited after it settled only once it settles again", async () => {
    const { choice, suggestions } = harness();
    choice.retarget(LOCAL);
    choice.draft(DRAFT);
    choice.flush();
    choice.draft(`${DRAFT} after a reconnect`);
    await vi.advanceTimersByTimeAsync(0);
    expect(suggestions).toEqual([]);

    await settle();
    expect(suggestions).toMatchObject([{ prompt: `${DRAFT} after a reconnect` }]);
  });

  it("keeps what it read, and the pick, when a re-read fails", async () => {
    const { choice, state, catalogs } = harness();
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.pick("quick-fix");

    catalogs.delete("local");
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);

    expect(state()).toMatchObject({
      workflow: "quick-fix",
      problem: "Couldn't list workflows: offline",
      status: "ready",
    });
    expect(state().options).toHaveLength(3);
  });

  it("owes the next draft its own suggestion once this one is submitted", async () => {
    const { choice, state, suggestions, answer, catalogs } = harness();
    catalogs.set("local", catalog({ default: "spec-driven" }));
    choice.retarget(LOCAL);
    await vi.advanceTimersByTimeAsync(0);
    choice.draft(DRAFT);
    choice.flush();
    await answer(applies("quick-fix"));

    choice.reset();
    expect(state().workflow).toBe("spec-driven");
    choice.draft(DRAFT);
    choice.flush();

    expect(suggestions).toHaveLength(2);
  });
});
