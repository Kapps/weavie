// A walkthrough code block: a read-only editor on the live file, its recorded lines mapped onto the file now and the
// diff from the first copy painted in. Folds are the code view's bands; notes and questions are cards between lines.

import { type Accessor, createMemo, createRoot, type JSX, Match, Show, Switch } from "solid-js";
import type { WalkthroughCode, WalkthroughFile } from "../../bridge";
import { type CodeBand, createCodeView } from "../../editor/code-view";
import { settledContentVersion } from "../../editor/content-version";
import { diffLines, type LineChanges, mapLine } from "../../editor/line-anchor";
import { monaco } from "../../editor/monaco-setup";
import { computeTextDiffLines } from "../../editor/review/diff-computation";
import { computeDiffMarkers } from "../../editor/review/diff-markers";
import { createDiffPaint } from "../../editor/review/diff-zones";
import { selectionExcerpt, selectionQuote } from "../../editor/selection-excerpt";
import { watchSelectionGestures } from "../../editor/selection-gestures";
import { createZoneCards, type ZoneCard } from "../../editor/zone-cards";
import type { SelectionActions } from "../selection-actions";

const HIDDEN_AREAS_SOURCE = "weavie.walkthrough";

/** A question's card under a block line; `line` is numbered in the block's recorded copy. */
export interface QuestionCard {
  key: string;
  line: number;
  quote: string;
  render: () => JSX.Element;
}

type LineCard =
  | { kind: "note"; key: string; line: number; text: string; was: string | null; indent: number }
  | ({ kind: "question" } & QuestionCard);

/** The model a block shows: the live file, or, once the file is gone, a copy of what it recorded. */
export interface BlockModel {
  model: monaco.editor.ITextModel;
  deleted: boolean;
}

export interface WalkthroughCodeOptions {
  mount: HTMLElement;
  code: WalkthroughCode;
  file: WalkthroughFile;
  source: (hash: string) => Promise<string>;
  open: (code: WalkthroughCode, copy: string) => Promise<BlockModel>;
  /** The folds the reader opened, by first line. */
  openFolds: Accessor<ReadonlySet<number>>;
  toggleFold: (from: number) => void;
  questions: Accessor<readonly QuestionCard[]>;
  actions: Pick<SelectionActions, "clear" | "moved">;
  /** The reader selected `text`, ending on `line` of the recorded copy. */
  onSelect: (text: string, quote: string, line: number, point: { x: number; y: number }) => void;
  onNotices: (notices: readonly string[]) => void;
}

const sameSet = (a: ReadonlySet<number>, b: ReadonlySet<number>): boolean =>
  a.size === b.size && [...a].every((value) => b.has(value));

/** Builds the block's editor once its texts load; the returned function disposes it. */
export async function createWalkthroughCode(options: WalkthroughCodeOptions): Promise<() => void> {
  const { code, file, mount } = options;
  const [copy, before, afterText] = await Promise.all([
    options.source(code.source),
    options.source(file.before),
    file.after ? options.source(file.after) : null,
  ]);
  const { model, deleted } = await options.open(code, copy);
  const view = createCodeView({
    mount,
    widgetsParent: document.body,
    model,
    dimension: { width: mount.clientWidth, height: mount.clientHeight },
    overrides: {
      readOnly: true,
      domReadOnly: true,
      glyphMargin: false,
      renderLineHighlight: "none",
      wordWrap: "on",
      wrappingIndent: "deepIndent",
      scrollbar: { vertical: "hidden", handleMouseWheel: false, alwaysConsumeMouseWheel: false },
      padding: { top: 4, bottom: 4 },
    },
    hiddenSource: HIDDEN_AREAS_SOURCE,
    onHeight: (height) => {
      mount.style.height = `${height}px`;
    },
    onClick: () => {},
  });
  const { editor } = view;
  return createRoot((dispose) => {
    const settled = settledContentVersion(model);
    const live = createMemo(() => {
      settled.version();
      return model.getValue();
    });
    const openFolds = createMemo(() => options.openFolds(), new Set<number>(), {
      equals: sameSet,
    });
    // The recorded copy mapped onto the file now; null when the file is too large to compare.
    const map = createMemo((): LineChanges | null => {
      if (deleted) return [];
      try {
        return diffLines(copy, live());
      } catch {
        return null;
      }
    });
    const at = (line: number) => mapLine(map() ?? [], line);
    // The live lines shown: the block, less its closed folds.
    const shown = createMemo(() => {
      const first = at(code.from).afterLine;
      const last = Math.max(first, at(code.to).afterLine);
      const folds = (code.folds ?? []).map((fold) => ({
        fold,
        open: openFolds().has(fold.from),
        from: at(fold.from).afterLine,
        to: at(fold.to).afterLine,
      }));
      return { first, last, folds };
    });
    const copyLines = copy.split(/\r?\n/);
    const decorations = editor.createDecorationsCollection();
    const diffPaint = createDiffPaint(editor, (change) => editor.changeViewZones(change));
    const questions = createMemo(() => options.questions(), [], {
      equals: (a, b) =>
        a.length === b.length &&
        a.every((card, index) => card.key === b[index]!.key && card.line === b[index]!.line),
    });

    // Hidden lines, the diff and the marks: only the text, the folds or the questions change them.
    createMemo(() => {
      const changes = map();
      const lineCount = model.getLineCount();
      const { first, last, folds } = shown();
      const hidden = folds
        .filter((fold) => !fold.open)
        .map(({ from, to }) => new monaco.Range(from, 1, to, 1));
      if (first > 1) hidden.push(new monaco.Range(1, 1, first - 1, 1));
      if (last < lineCount) hidden.push(new monaco.Range(last + 1, 1, lineCount, 1));
      const bands = folds.map(
        ({ fold, open, from }): CodeBand => ({
          beforeLine: from,
          label: open ? `Hide · ${fold.label}` : `${fold.to - fold.from + 1} lines · ${fold.label}`,
          title: open ? "Hide These Lines" : "Show These Lines",
          open: () => options.toggleFold(fold.from),
        }),
      );
      view.setHidden(hidden, bands);
      const marks: monaco.editor.IModelDeltaDecoration[] = [];
      const mark = (line: number, token: string, className: string): boolean => {
        const anchor = at(line);
        const column = anchor.exact ? model.getLineContent(anchor.afterLine).indexOf(token) : -1;
        if (column >= 0) {
          marks.push({
            range: new monaco.Range(
              anchor.afterLine,
              column + 1,
              anchor.afterLine,
              column + 1 + token.length,
            ),
            options: { inlineClassName: className },
          });
        }
        return column >= 0;
      };
      const lost = (code.notes ?? []).filter(
        (note) => !mark(note.line, note.token, "walkthrough-mark"),
      );
      for (const question of questions()) mark(question.line, question.quote, "walkthrough-asked");
      const diff = !deleted && changes !== null ? computeTextDiffLines(before, live()) : null;
      const markers =
        diff === null
          ? null
          : computeDiffMarkers(
              { original: before, acceptedBaseline: undefined, claudeVersion: undefined },
              { changes: [...diff], userChanges: [], fadedChanges: [] },
            );
      decorations.set(marks);
      if (markers === null) diffPaint.clear();
      else diffPaint.paint(markers);
      const notices: (string | false)[] = [
        deleted && "Deleted since this walkthrough recorded it",
        changes === null && "Too large to compare",
        file.late === true && "Recorded after edits to this file began",
        afterText !== null && live() !== afterText && "Changed since this walkthrough was updated",
        lost.length > 0 && "A noted line changed",
      ];
      options.onNotices(notices.filter((notice): notice is string => notice !== false));
    });

    const cards = createMemo((): ZoneCard<LineCard>[] => {
      map();
      const list: LineCard[] = [
        ...(code.notes ?? []).flatMap((note): LineCard[] =>
          at(note.line).exact
            ? [
                {
                  kind: "note",
                  key: `note:${note.line}:${note.token}`,
                  line: note.line,
                  text: note.text,
                  was: note.was ?? null,
                  indent: (copyLines[note.line - 1] ?? "").match(/^\s*/)![0].replace(/\t/g, "    ")
                    .length,
                },
              ]
            : [],
        ),
        ...questions().map((question): LineCard => ({ kind: "question", ...question })),
      ];
      return list.map((card) => ({
        key: card.key,
        afterLine: at(card.line).afterLine,
        data: card,
      }));
    });
    const removeCards = createZoneCards(editor, cards, (card) => <Card card={card()} />, 0, true);

    // Asking is from a selection, once its gesture completes; the copy's line keeps the question in place.
    const unwatch = watchSelectionGestures(editor, options.actions, (selection, point) => {
      const quoted = selectionQuote(model, selection);
      if (map() === null || quoted === null) {
        options.actions.clear();
        return;
      }
      const last = quoted.range.endLineNumber;
      const line = deleted ? last : mapLine(diffLines(live(), copy), last).afterLine;
      options.onSelect(selectionExcerpt(model, selection).text, quoted.quote, line, point);
    });

    return () => {
      settled.dispose();
      unwatch();
      removeCards();
      dispose();
      view.dispose();
    };
  });
}

function Card(props: { card: LineCard }): JSX.Element {
  return (
    <Switch>
      <Match when={props.card.kind === "question" && props.card}>{(card) => card().render()}</Match>
      <Match when={props.card.kind === "note" && props.card}>
        {(note) => (
          <div class="walkthrough-note" style={{ "padding-left": `${note().indent}ch` }}>
            {note().text}
            <Show when={note().was}>
              <span class="walkthrough-was">Was: {note().was}</span>
            </Show>
          </div>
        )}
      </Match>
    </Switch>
  );
}
