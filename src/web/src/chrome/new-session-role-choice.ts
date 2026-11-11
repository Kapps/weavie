import type { RoleControls, RolePick, RoleSuggestion, WorkflowRoleNeed } from "../bridge";
import type { AgentControlAxis } from "../messaging/protocol-types";
import { errorMessage, type WorkflowTarget } from "./new-session-workflow-choice";

/** The roles a start would need models for: those the shown workflow asks for that the provider can't yet fill. */
export interface RolePair {
  target: WorkflowTarget;
  workflow: string;
  roles: readonly WorkflowRoleNeed[];
}

export interface RoleRow {
  role: string;
  /** The model a start saves for the role; null until one is picked or suggested. */
  model: string | null;
  /** The option values picked with the model, by the provider's ids; an option left on Default is absent. */
  options: Readonly<Record<string, string>>;
  /** The pickers as they stand; undefined while the agent is being asked for the models. */
  axes: readonly AgentControlAxis[] | undefined;
  problem: string | null;
  suggesting: boolean;
}

export interface RoleChoiceState {
  rows: readonly RoleRow[];
  /** Why the provider's models couldn't be listed. */
  error: string | null;
}

type ControlsRequest = (
  target: WorkflowTarget,
  model: string,
  signal: AbortSignal,
) => Promise<RoleControls>;

type SuggestionRequest = (
  pair: RolePair,
  role: string,
  signal: AbortSignal,
) => Promise<RoleSuggestion>;

const MODEL = "model";

/** Whether every role a start needs has a model. */
export const rolesReady = (state: RoleChoiceState): boolean =>
  state.rows.every((row) => row.model !== null);

// The agent's pickers with the row's own picks shown on them.
const shown = (
  base: readonly AgentControlAxis[],
  model: string | null,
  options: Readonly<Record<string, string>>,
): AgentControlAxis[] =>
  base.map((axis) => {
    const value = axis.category === MODEL ? (model ?? "") : (options[axis.id] ?? "");
    const picked = axis.options.find((option) => option.id === value);
    return value === axis.value || picked === undefined
      ? axis
      : { ...axis, value, valueLabel: picked.label };
  });

/**
 * Owns the pickers for the roles a start still needs: the model, then the effort and Fast Mode that model offers,
 * as the agent reports them; one model suggestion for each role with nothing saved; and the user's picks, which
 * nothing automatic writes over. A new pair starts over.
 */
export class NewSessionRoleChoice {
  private pair: RolePair | null = null;
  private key: string | null = null;
  private load = new AbortController();
  /** The agent's pickers by chosen model ("" for none), asked once per pair. */
  private readonly controls = new Map<string, Promise<RoleControls>>();
  private readonly base = new Map<string, readonly AgentControlAxis[]>();
  private readonly suggestions = new Map<string, AbortController>();
  private readonly picked = new Set<string>();
  private state: RoleChoiceState = { rows: [], error: null };

  constructor(
    private readonly requestControls: ControlsRequest,
    private readonly requestSuggestion: SuggestionRequest,
    private readonly changed: (state: RoleChoiceState) => void,
  ) {}

  /** Shows the pickers `pair` needs; null for none. The same pair again changes nothing unless its models failed to list. */
  show(pair: RolePair | null): void {
    const key = pair === null ? "" : JSON.stringify([pair.target, pair.workflow, pair.roles]);
    if (key === this.key && this.state.error === null) {
      return;
    }

    this.cancel();
    this.key = key;
    this.pair = pair;
    this.load = new AbortController();
    this.controls.clear();
    this.base.clear();
    this.picked.clear();
    const rows = (pair?.roles ?? []).map((need) => ({
      role: need.role,
      model: null,
      options: {},
      axes: undefined,
      problem: need.problem,
      suggesting: false,
    }));
    this.publish({ rows, error: null });
    if (pair === null || rows.length === 0) {
      return;
    }

    const load = this.load;
    this.ask(pair.target, "").then((result) => {
      if (this.load !== load) {
        return;
      }
      if (result.axes.length === 0) {
        this.publish({ error: `Couldn't list models: ${result.error ?? "none offered"}` });
        return;
      }
      for (const row of this.state.rows) {
        this.base.set(row.role, result.axes);
        this.update(row.role, {});
        // A saved model that has gone is the user's to replace; only a role with nothing saved is suggested.
        if (row.problem === null) this.suggest(row.role);
      }
    });
  }

  /** The user chose `value` on a row's `axisId` picker; nothing automatic writes over it. */
  pick(role: string, axisId: string, value: string): void {
    const row = this.state.rows.find((candidate) => candidate.role === role);
    const axis = row?.axes?.find((candidate) => candidate.id === axisId);
    if (row === undefined || axis === undefined) {
      return;
    }
    if (axis.category === MODEL) {
      this.picked.add(role);
      this.dropSuggestion(role);
      this.setModel(role, value);
      return;
    }
    const { [axisId]: _, ...rest } = row.options;
    this.update(role, { options: value === "" ? rest : { ...rest, [axisId]: value } });
  }

  /** Each role's model and options as shown now; a suggestion still on its way is discarded. */
  capture(): Record<string, RolePick> {
    for (const role of [...this.suggestions.keys()]) {
      this.dropSuggestion(role);
      this.update(role, { suggesting: false });
    }
    return Object.fromEntries(
      this.state.rows.flatMap((row) =>
        row.model === null ? [] : [[row.role, { model: row.model, options: { ...row.options } }]],
      ),
    );
  }

  cancel(): void {
    this.load.abort();
    for (const role of [...this.suggestions.keys()]) this.dropSuggestion(role);
  }

  private ask(target: WorkflowTarget, model: string): Promise<RoleControls> {
    let asked = this.controls.get(model);
    if (asked === undefined) {
      asked = this.requestControls(target, model, this.load.signal).catch((error: unknown) => ({
        axes: [],
        error: errorMessage(error),
      }));
      this.controls.set(model, asked);
    }
    return asked;
  }

  // The options belong to the model, so a new model starts with none and asks the agent what it offers.
  private setModel(role: string, model: string): void {
    const pair = this.pair;
    if (pair === null) {
      return;
    }
    this.base.set(
      role,
      (this.base.get(role) ?? []).filter((axis) => axis.category === MODEL),
    );
    this.update(role, { model, options: {}, problem: null, suggesting: false });
    const load = this.load;
    this.ask(pair.target, model).then((result) => {
      const row = this.state.rows.find((candidate) => candidate.role === role);
      if (this.load !== load || row?.model !== model) {
        return;
      }
      if (result.axes.length > 0) this.base.set(role, result.axes);
      this.update(
        role,
        result.error === null ? {} : { problem: `Couldn't get the options: ${result.error}` },
      );
    });
  }

  private suggest(role: string): void {
    const pair = this.pair;
    if (pair === null) {
      return;
    }
    const ask = new AbortController();
    this.suggestions.set(role, ask);
    this.update(role, { suggesting: true });
    this.requestSuggestion(pair, role, ask.signal).then(
      (result) => this.suggestionLanded(role, ask, result),
      (error: unknown) =>
        this.suggestionLanded(role, ask, {
          applies: false,
          model: null,
          error: errorMessage(error),
        }),
    );
  }

  private suggestionLanded(role: string, ask: AbortController, result: RoleSuggestion): void {
    if (this.suggestions.get(role) !== ask) {
      return;
    }

    this.suggestions.delete(role);
    const offered = this.base
      .get(role)
      ?.find((axis) => axis.category === MODEL)
      ?.options.some((option) => option.id === result.model);
    if (result.applies && result.model !== null && offered === true && !this.picked.has(role)) {
      this.setModel(role, result.model);
      return;
    }
    this.update(role, {
      suggesting: false,
      ...(result.error === null ? {} : { problem: `Model suggestion failed: ${result.error}` }),
    });
  }

  private dropSuggestion(role: string): void {
    this.suggestions.get(role)?.abort();
    this.suggestions.delete(role);
  }

  private update(role: string, change: Partial<RoleRow>): void {
    const base = this.base.get(role);
    this.publish({
      rows: this.state.rows.map((row) => {
        if (row.role !== role) {
          return row;
        }
        const next = { ...row, ...change };
        return {
          ...next,
          axes: base === undefined ? undefined : shown(base, next.model, next.options),
        };
      }),
    });
  }

  private publish(change: Partial<RoleChoiceState>): void {
    this.state = { ...this.state, ...change };
    this.changed(this.state);
  }
}
