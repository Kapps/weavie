import type { WorkflowStartCatalog, WorkflowStartOption, WorkflowSuggestion } from "../bridge";
import { DraftSettleTimer, draftWorthQuerying } from "./new-session-draft-settle";

/** Where a new session would start: the host, and the session it branches from (null for the workspace). */
export interface WorkflowTarget {
  backendId: string;
  sourceId: string | null;
}

export interface WorkflowChoiceState {
  options: readonly WorkflowStartOption[];
  /** The workflow a start would run; null for none. */
  workflow: string | null;
  problem: string | null;
  status: "loading" | "ready" | "suggesting";
}

type CatalogRequest = (
  target: WorkflowTarget,
  signal: AbortSignal,
) => Promise<WorkflowStartCatalog>;

type SuggestionRequest = (
  target: WorkflowTarget,
  prompt: string,
  signal: AbortSignal,
) => Promise<WorkflowSuggestion>;

const sameTarget = (left: WorkflowTarget | null, right: WorkflowTarget | null): boolean =>
  left === right ||
  (left !== null &&
    right !== null &&
    left.backendId === right.backendId &&
    left.sourceId === right.sourceId);

const message = (error: unknown): string =>
  error instanceof Error ? error.message : String(error);

/**
 * Owns the workflow a new session starts with: the configured default, until one suggestion for the composed
 * prompt or the user's own pick replaces it. The suggestion is asked once per target, never for a draft too
 * short to judge, never once the user has picked, and never lands over a value a start has already taken.
 */
export class NewSessionWorkflowChoice {
  private target: WorkflowTarget | null = null;
  private prompt = "";
  /** The user chose a workflow for this draft, so no suggestion is wanted for it. */
  private picked = false;
  /** The value is a pick or an applied suggestion, kept across a re-read while the catalog still offers it. */
  private held = false;
  private online = false;
  private asked = false;
  private due = false;
  private loaded = false;
  private fallback: string | null = null;
  private catalogProblem: string | null = null;
  private suggestionError: string | null = null;
  private catalogLoad: AbortController | null = null;
  private suggestion: AbortController | null = null;
  private readonly settle = new DraftSettleTimer(() => this.settled());
  private state: WorkflowChoiceState = {
    options: [],
    workflow: null,
    problem: null,
    status: "loading",
  };

  constructor(
    private readonly requestCatalog: CatalogRequest,
    private readonly requestSuggestion: SuggestionRequest,
    private readonly changed: (state: WorkflowChoiceState) => void,
  ) {}

  /**
   * Reads the catalog `target` offers; null while its host is unreachable, which changes nothing but waits.
   * A different target owes the draft a suggestion of its own.
   */
  retarget(target: WorkflowTarget | null): void {
    this.catalogLoad?.abort();
    this.catalogLoad = null;
    this.online = target !== null;
    if (target === null) {
      return;
    }
    if (!sameTarget(this.target, target)) {
      // A draft that had earned its suggestion is owed one from the new catalog too.
      const owed = this.asked || this.due;
      this.target = target;
      this.dropSuggestion();
      this.asked = false;
      this.due = owed;
      this.loaded = false;
      this.held = this.picked;
      this.catalogProblem = null;
      this.publish({ options: [], status: "loading" });
    }

    const load = new AbortController();
    this.catalogLoad = load;
    this.requestCatalog(target, load.signal).then(
      (catalog) => this.catalogLanded(load, catalog),
      (error: unknown) =>
        this.catalogLanded(load, {
          workflows: [],
          default: null,
          defaultProblem: `Couldn't list workflows: ${message(error)}`,
        }),
    );
  }

  /** The prompt as it stands; a draft worth judging that then sits unchanged gets the one suggestion. */
  draft(prompt: string): void {
    this.prompt = prompt;
    if (this.mayAsk) {
      this.settle.arm(prompt);
    } else {
      this.settle.clear();
    }
  }

  /** Focus left the prompt, so no further typing is coming: suggest now if the draft is worth judging. */
  flush(): void {
    this.settle.clear();
    if (draftWorthQuerying(this.prompt)) {
      this.settled();
    }
  }

  /** The user chose `workflow` (null for none); nothing automatic writes over it. */
  pick(workflow: string | null): void {
    this.picked = true;
    this.held = true;
    this.settle.clear();
    this.dropSuggestion();
    this.publish({ workflow, status: this.loaded ? "ready" : "loading" });
  }

  /** The workflow a start runs, as shown now; a suggestion still on its way is discarded. */
  capture(): string | null {
    this.settle.clear();
    this.due = false;
    if (this.suggestion !== null) {
      this.dropSuggestion();
      this.publish({ status: "ready" });
    }
    return this.state.workflow;
  }

  /** The draft was submitted: the next one starts from the default and is owed its own suggestion. */
  reset(): void {
    this.prompt = "";
    this.picked = false;
    this.held = false;
    this.asked = false;
    this.settle.clear();
    this.dropSuggestion();
    this.publish({ workflow: this.fallback, status: this.loaded ? "ready" : "loading" });
  }

  cancel(): void {
    this.settle.clear();
    this.catalogLoad?.abort();
    this.dropSuggestion();
  }

  private get mayAsk(): boolean {
    return !this.asked && !this.picked;
  }

  private selectable(workflow: string | null): boolean {
    return (
      workflow === null ||
      this.state.options.some((option) => option.id === workflow && option.error === null)
    );
  }

  private catalogLanded(load: AbortController, catalog: WorkflowStartCatalog): void {
    if (this.catalogLoad !== load) {
      return;
    }

    this.catalogLoad = null;
    this.loaded = true;
    this.fallback = catalog.default;
    this.catalogProblem = catalog.defaultProblem;
    this.state = { ...this.state, options: catalog.workflows };
    this.held &&= this.selectable(this.state.workflow);
    if (!this.held) {
      this.state = { ...this.state, workflow: catalog.default };
    }
    this.publish({ status: this.suggestion === null ? "ready" : "suggesting" });
    this.ask();
  }

  private settled(): void {
    this.due = true;
    this.ask();
  }

  private ask(): void {
    const target = this.target;
    if (!this.due || !this.mayAsk || !this.loaded || !this.online || target === null) {
      return;
    }
    // A draft cut short since it settled, or a catalog with nothing startable, stays owed its suggestion.
    if (
      !draftWorthQuerying(this.prompt) ||
      !this.state.options.some((option) => option.error === null)
    ) {
      return;
    }

    this.due = false;
    this.asked = true;
    const suggestion = new AbortController();
    this.suggestion = suggestion;
    this.suggestionError = null;
    this.publish({ status: "suggesting" });
    this.requestSuggestion(target, this.prompt, suggestion.signal).then(
      (result) => this.suggestionLanded(suggestion, result),
      (error: unknown) =>
        this.suggestionLanded(suggestion, {
          applies: false,
          workflow: null,
          error: message(error),
        }),
    );
  }

  private suggestionLanded(suggestion: AbortController, result: WorkflowSuggestion): void {
    if (this.suggestion !== suggestion) {
      return;
    }

    this.suggestion = null;
    this.suggestionError = result.error;
    if (result.applies && this.selectable(result.workflow)) {
      this.held = true;
      this.state = { ...this.state, workflow: result.workflow };
    }
    this.publish({ status: "ready" });
  }

  private dropSuggestion(): void {
    this.due = false;
    this.suggestion?.abort();
    this.suggestion = null;
    this.suggestionError = null;
  }

  private publish(change: Partial<WorkflowChoiceState>): void {
    const problem =
      this.suggestionError === null
        ? this.catalogProblem
        : `Workflow suggestion failed: ${this.suggestionError}`;
    this.state = { ...this.state, ...change, problem };
    this.changed(this.state);
  }
}
