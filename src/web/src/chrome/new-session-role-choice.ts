import type { RoleModels, RoleSuggestion, WorkflowRoleNeed } from "../bridge";
import type { AgentControlOption } from "../messaging/protocol-types";
import type { WorkflowTarget } from "./new-session-workflow-choice";

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
  problem: string | null;
  suggesting: boolean;
}

export interface RoleChoiceState {
  rows: readonly RoleRow[];
  /** The provider's models; null while they are being listed. */
  models: readonly AgentControlOption[] | null;
  /** Why the models couldn't be listed. */
  error: string | null;
}

type ModelsRequest = (target: WorkflowTarget, signal: AbortSignal) => Promise<RoleModels>;

type SuggestionRequest = (
  pair: RolePair,
  role: string,
  signal: AbortSignal,
) => Promise<RoleSuggestion>;

const message = (error: unknown): string =>
  error instanceof Error ? error.message : String(error);

/** Whether every role a start needs has a model. */
export const rolesReady = (state: RoleChoiceState): boolean =>
  state.rows.every((row) => row.model !== null);

/**
 * Owns the model pickers for the roles a start still needs: the provider's models, one suggestion for each role with
 * nothing saved, and the user's picks, which nothing automatic writes over. A new pair starts over.
 */
export class NewSessionRoleChoice {
  private pair: RolePair | null = null;
  private key: string | null = null;
  private load: AbortController | null = null;
  private readonly suggestions = new Map<string, AbortController>();
  private readonly picked = new Set<string>();
  private state: RoleChoiceState = { rows: [], models: [], error: null };

  constructor(
    private readonly requestModels: ModelsRequest,
    private readonly requestSuggestion: SuggestionRequest,
    private readonly changed: (state: RoleChoiceState) => void,
  ) {}

  /** Shows the pickers `pair` needs; null for none. The same pair again changes nothing. */
  show(pair: RolePair | null): void {
    const key = pair === null ? "" : JSON.stringify([pair.target, pair.workflow, pair.roles]);
    if (key === this.key) {
      return;
    }

    this.cancel();
    this.key = key;
    this.pair = pair;
    this.picked.clear();
    const rows = (pair?.roles ?? []).map((need) => ({
      role: need.role,
      model: null,
      problem: need.problem,
      suggesting: false,
    }));
    this.publish({ rows, models: rows.length === 0 ? [] : null, error: null });
    if (pair === null || rows.length === 0) {
      return;
    }

    const load = new AbortController();
    this.load = load;
    this.requestModels(pair.target, load.signal).then(
      (result) => this.modelsLanded(load, result),
      (error: unknown) => this.modelsLanded(load, { models: [], error: message(error) }),
    );
  }

  /** The user chose `model` for `role`; nothing automatic writes over it. */
  pick(role: string, model: string): void {
    this.picked.add(role);
    this.dropSuggestion(role);
    this.update(role, { model, problem: null, suggesting: false });
  }

  /** Each role's model as shown now; a suggestion still on its way is discarded. */
  capture(): Record<string, string> {
    for (const role of [...this.suggestions.keys()]) {
      this.dropSuggestion(role);
      this.update(role, { suggesting: false });
    }
    return Object.fromEntries(
      this.state.rows.flatMap((row) => (row.model === null ? [] : [[row.role, row.model]])),
    );
  }

  cancel(): void {
    this.load?.abort();
    this.load = null;
    for (const role of [...this.suggestions.keys()]) this.dropSuggestion(role);
  }

  private modelsLanded(load: AbortController, result: RoleModels): void {
    if (this.load !== load) {
      return;
    }

    this.load = null;
    this.publish({
      models: result.models,
      error: result.error === null ? null : `Couldn't list models: ${result.error}`,
    });
    // A saved model that has gone is the user's to replace; only a role with nothing saved is suggested.
    for (const row of this.state.rows) {
      if (row.problem === null && result.error === null) this.suggest(row.role);
    }
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
        this.suggestionLanded(role, ask, { applies: false, model: null, error: message(error) }),
    );
  }

  private suggestionLanded(role: string, ask: AbortController, result: RoleSuggestion): void {
    if (this.suggestions.get(role) !== ask) {
      return;
    }

    this.suggestions.delete(role);
    const offered = this.state.models?.some((model) => model.id === result.model) === true;
    this.update(role, {
      suggesting: false,
      ...(result.applies && offered && !this.picked.has(role) ? { model: result.model } : {}),
      ...(result.error === null ? {} : { problem: `Model suggestion failed: ${result.error}` }),
    });
  }

  private dropSuggestion(role: string): void {
    this.suggestions.get(role)?.abort();
    this.suggestions.delete(role);
  }

  private update(role: string, change: Partial<RoleRow>): void {
    this.publish({
      rows: this.state.rows.map((row) => (row.role === role ? { ...row, ...change } : row)),
    });
  }

  private publish(change: Partial<RoleChoiceState>): void {
    this.state = { ...this.state, ...change };
    this.changed(this.state);
  }
}
