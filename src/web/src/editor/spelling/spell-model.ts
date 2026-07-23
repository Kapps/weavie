import type { SpellIssue } from "../../bridge";
import { monaco } from "../monaco-setup";
import { changedLineNumbers } from "./spell-protocol";
import type { RestoredSpellLine, SpellContext } from "./spell-types";

/** One Monaco working copy's authored-line anchors and checked results. */
export class SpellModel {
  readonly anchors = new Map<string, string>();
  readonly issues = new Map<string, SpellIssue[]>();
  readonly needsCheck = new Set<string>();
  readonly latestRequest = new Map<string, string>();
  modelEpoch: string;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly subscriptions: { dispose(): void }[] = [];

  constructor(
    readonly model: monaco.editor.ITextModel,
    private readonly nextEpoch: () => string,
    private readonly nextAnchor: () => string,
  ) {
    this.modelEpoch = nextEpoch();
  }

  track(
    isDirty: () => boolean,
    onChanged: () => void,
    onCleared: () => void,
    onDispose: () => void,
  ): void {
    this.subscriptions.push(
      this.model.onDidChangeContent((event) => {
        if (event.isFlush || !isDirty()) {
          this.clear();
          onCleared();
          return;
        }
        for (const line of changedLineNumbers(event.changes, this.model.getLineCount())) {
          const anchorId = this.anchorAtLine(line);
          if (anchorId !== null) {
            this.issues.delete(anchorId);
            this.needsCheck.add(anchorId);
          }
        }
        onChanged();
      }),
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

  clearTimer(): void {
    this.cancelTimer();
  }

  clear(): void {
    this.cancelTimer();
    this.model.deltaDecorations([...this.anchors.values()], []);
    this.anchors.clear();
    this.issues.clear();
    this.needsCheck.clear();
    this.latestRequest.clear();
    this.modelEpoch = this.nextEpoch();
  }

  invalidate(): void {
    this.cancelTimer();
    this.issues.clear();
    this.latestRequest.clear();
    this.modelEpoch = this.nextEpoch();
  }

  /** Merges Core-authored lines that still exactly match this working copy, returning their stable anchors. */
  restoreAuthoredLines(lines: readonly RestoredSpellLine[]): string[] {
    const anchors: string[] = [];
    for (const restored of lines) {
      if (
        restored.line < 1 ||
        restored.line > this.model.getLineCount() ||
        this.model.getLineContent(restored.line) !== restored.text
      ) {
        continue;
      }
      const anchorId = this.anchorAtLine(restored.line);
      if (anchorId !== null) {
        anchors.push(anchorId);
      }
    }
    return anchors;
  }

  dispose(): void {
    this.release(true);
  }

  private release(disposeSubscriptions: boolean): void {
    this.cancelTimer();
    if (disposeSubscriptions) {
      for (const subscription of this.subscriptions) {
        subscription.dispose();
      }
    }
    this.subscriptions.length = 0;
    this.anchors.clear();
    this.issues.clear();
    this.needsCheck.clear();
    this.latestRequest.clear();
  }

  anchorText(anchorId: string): string | null {
    const line = this.anchorLine(anchorId);
    return line === null ? null : this.model.getLineContent(line);
  }

  contextAt(position: monaco.IPosition | null): SpellContext | null {
    if (position === null) {
      return null;
    }
    for (const [anchorId, issues] of this.issues) {
      if (this.anchorLine(anchorId) !== position.lineNumber) {
        continue;
      }
      const issue = issues.find(
        (item) => position.column >= item.startColumn && position.column <= item.endColumn,
      );
      if (issue !== undefined) {
        return { ...issue, modelEpoch: this.modelEpoch };
      }
    }
    return null;
  }

  isCurrentContext(context: SpellContext): boolean {
    if (this.modelEpoch !== context.modelEpoch || !this.validIssue(context)) {
      return false;
    }
    return (this.issues.get(context.anchorId) ?? []).some(
      (issue) =>
        issue.word === context.word &&
        issue.startColumn === context.startColumn &&
        issue.endColumn === context.endColumn,
    );
  }

  validIssue(issue: SpellIssue | SpellContext): boolean {
    const line = this.anchorLine(issue.anchorId);
    if (line === null || issue.startColumn < 1 || issue.endColumn <= issue.startColumn) {
      return false;
    }
    if (issue.endColumn > this.model.getLineMaxColumn(line)) {
      return false;
    }
    return (
      this.model.getValueInRange(
        new monaco.Range(line, issue.startColumn, line, issue.endColumn),
      ) === issue.word
    );
  }

  decorations(): monaco.editor.IModelDeltaDecoration[] {
    const decorations: monaco.editor.IModelDeltaDecoration[] = [];
    for (const issues of this.issues.values()) {
      for (const issue of issues) {
        if (!this.validIssue(issue)) {
          continue;
        }
        const line = this.anchorLine(issue.anchorId);
        if (line !== null) {
          decorations.push({
            range: new monaco.Range(line, issue.startColumn, line, issue.endColumn),
            options: { inlineClassName: "weavie-spell-issue" },
          });
        }
      }
    }
    return decorations;
  }

  anchorLine(anchorId: string): number | null {
    const decorationId = this.anchors.get(anchorId);
    const range = decorationId === undefined ? null : this.model.getDecorationRange(decorationId);
    if (range !== null) {
      return range.startLineNumber;
    }
    if (decorationId !== undefined) {
      this.anchors.delete(anchorId);
      this.issues.delete(anchorId);
      this.needsCheck.delete(anchorId);
      this.latestRequest.delete(anchorId);
    }
    return null;
  }

  private anchorAtLine(line: number): string | null {
    for (const anchorId of this.anchors.keys()) {
      if (this.anchorLine(anchorId) === line) {
        return anchorId;
      }
    }
    const decorationId = this.model.deltaDecorations(
      [],
      [
        {
          range: new monaco.Range(line, 1, line, this.model.getLineMaxColumn(line)),
          options: { stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges },
        },
      ],
    )[0];
    if (decorationId === undefined) {
      return null;
    }
    const anchorId = this.nextAnchor();
    this.anchors.set(anchorId, decorationId);
    return anchorId;
  }

  private cancelTimer(): void {
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
      this.timer = undefined;
    }
  }
}
