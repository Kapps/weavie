import type { SpellIssue } from "../../bridge";
import { monaco } from "../monaco-setup";
import type { SpellContext } from "./spell-types";

/** Spell diagnostics and debounce state for one Monaco working copy. */
export class SpellModel {
  private issues: SpellIssue[] = [];
  private timer: ReturnType<typeof setTimeout> | undefined;
  private documentRevision: number | null = null;
  private readonly subscriptions: { dispose(): void }[] = [];

  constructor(readonly model: monaco.editor.ITextModel) {}

  track(onDispose: () => void): void {
    this.subscriptions.push(
      this.model.onWillDispose(() => {
        // Monaco owns listener teardown while it is disposing this model; do not dispose the active callback.
        this.release(false);
        onDispose();
      }),
    );
  }

  schedule(callback: () => void, delay: number): void {
    this.cancelTimer();
    this.timer = setTimeout(callback, delay);
  }

  clear(): void {
    this.cancelTimer();
    this.issues = [];
    this.documentRevision = null;
  }

  markSubmitted(documentRevision: number): void {
    this.documentRevision = documentRevision;
  }

  applyDiagnostics(issues: readonly SpellIssue[], documentRevision: number): boolean {
    if (documentRevision !== this.documentRevision) {
      return false;
    }
    this.issues = issues.filter((issue) => this.validIssue(issue));
    return true;
  }

  contextAt(position: monaco.IPosition | null): SpellContext | null {
    if (position === null) {
      return null;
    }
    const issue = this.issues.find(
      (candidate) =>
        candidate.line === position.lineNumber &&
        position.column >= candidate.startColumn &&
        position.column <= candidate.endColumn,
    );
    return issue === undefined ? null : { ...issue, modelId: this.model.id };
  }

  isCurrentContext(context: SpellContext): boolean {
    return (
      context.modelId === this.model.id &&
      this.validIssue(context) &&
      this.issues.some(
        (issue) =>
          issue.line === context.line &&
          issue.word === context.word &&
          issue.startColumn === context.startColumn &&
          issue.endColumn === context.endColumn,
      )
    );
  }

  decorations(): monaco.editor.IModelDeltaDecoration[] {
    return this.issues.map((issue) => ({
      range: new monaco.Range(issue.line, issue.startColumn, issue.line, issue.endColumn),
      options: { inlineClassName: "weavie-spell-issue" },
    }));
  }

  dispose(): void {
    this.release(true);
  }

  private validIssue(issue: SpellIssue): boolean {
    if (
      !Number.isInteger(issue.line) ||
      !Number.isInteger(issue.startColumn) ||
      !Number.isInteger(issue.endColumn) ||
      issue.line < 1 ||
      issue.line > this.model.getLineCount() ||
      issue.startColumn < 1 ||
      issue.endColumn <= issue.startColumn ||
      issue.endColumn > this.model.getLineMaxColumn(issue.line)
    ) {
      return false;
    }
    return (
      this.model.getValueInRange(
        new monaco.Range(issue.line, issue.startColumn, issue.line, issue.endColumn),
      ) === issue.word
    );
  }

  private release(disposeSubscriptions: boolean): void {
    this.cancelTimer();
    if (disposeSubscriptions) {
      for (const subscription of this.subscriptions) {
        subscription.dispose();
      }
    }
    this.subscriptions.length = 0;
    this.issues = [];
    this.documentRevision = null;
  }

  private cancelTimer(): void {
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
  }
}
