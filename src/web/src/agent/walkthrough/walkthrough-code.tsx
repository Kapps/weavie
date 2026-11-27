// A walkthrough code block: a read-only editor on the live file, its recorded lines mapped onto the file now and the
// diff from the first copy painted in. Notes, fold rows and questions are cards between lines, as inline replies are.

import {
  type Accessor,
  createMemo,
  createRoot,
  createSignal,
  type JSX,
  Match,
  Show,
  Switch,
} from "solid-js";
import type { WalkthroughCode, WalkthroughFile } from "../../bridge";
import { hiddenAreas } from "../../editor/hidden-areas";
import { diffLines, type LineChanges, mapLine } from "../../editor/line-anchor";
import { createEmbeddedEditor, embeddedEditorChrome, monaco } from "../../editor/monaco-setup";
import { computeTextDiffLines } from "../../editor/review/diff-computation";
import { computeDiffMarkers } from "../../editor/review/diff-markers";
import { addDiffZones, DIFF_RECOMPUTE_DEBOUNCE_MS } from "../../editor/review/diff-zones";
import { lastLineOf, selectionExcerpt } from "../../editor/selection-excerpt";
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
  | {
      kind: "fold";
      key: string;
      line: number;
      from: number;
      to: number;
      label: string;
      open: boolean;
    }
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
  const [copy, before] = await Promise.all([
    options.source(code.source),
    options.source(file.before),
  ]);
  const { model, deleted } = await options.open(code, copy);
  const widgets = document.createElement("div");
  widgets.className = "monaco-editor embedded-editor-overflow-widgets";
  document.body.append(widgets);
  const editor = createEmbeddedEditor(
    mount,
    model,
    {
      dimension: { width: mount.clientWidth, height: mount.clientHeight },
      overflowWidgetsDomNode: widgets,
    },
    {
      ...embeddedEditorChrome,
      readOnly: true,
      domReadOnly: true,
      glyphMargin: false,
      renderLineHighlight: "none",
      wordWrap: "on",
      wrappingIndent: "deepIndent",
      scrollbar: { vertical: "hidden", handleMouseWheel: false, alwaysConsumeMouseWheel: false },
      padding: { top: 4, bottom: 4 },
    },
  );
  return createRoot((dispose) => {
    const [live, setLive] = createSignal(model.getValue());
    let timer: ReturnType<typeof setTimeout> | undefined;
    const changed = model.onDidChangeContent(() => {
      clearTimeout(timer);
      timer = setTimeout(() => setLive(model.getValue()), DIFF_RECOMPUTE_DEBOUNCE_MS);
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
      const folded = (code.folds ?? [])
        .filter((fold) => !openFolds().has(fold.from))
        .map((fold) => [at(fold.from).afterLine, at(fold.to).afterLine] as const);
      return { first, last, folded };
    });
    const isShown = (line: number): boolean => {
      const { first, last, folded } = shown();
      return (
        line >= first && line <= last && !folded.some(([from, to]) => line >= from && line <= to)
      );
    };
    const copyLines = copy.split(/\r?\n/);
    const decorations = editor.createDecorationsCollection();
    let diffZones: string[] = [];
    const [afterText, setAfterText] = createSignal<string | null>(null);
    if (file.after) void options.source(file.after).then(setAfterText);
    const questions = createMemo(() => options.questions(), [], {
      equals: (a, b) =>
        a.length === b.length &&
        a.every((card, index) => card.key === b[index]!.key && card.line === b[index]!.line),
    });

    // Hidden lines, the diff and the marks: only the text, the folds or the questions change them.
    createMemo(() => {
      const changes = map();
      const lineCount = model.getLineCount();
      const { first, last, folded } = shown();
      const hidden: monaco.IRange[] = folded.map(([from, to]) => new monaco.Range(from, 1, to, 1));
      if (first > 1) hidden.push(new monaco.Range(1, 1, first - 1, 1));
      if (last < lineCount) hidden.push(new monaco.Range(last + 1, 1, lineCount, 1));
      hiddenAreas(editor).setHiddenAreas(hidden, HIDDEN_AREAS_SOURCE);
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
      decorations.set([...(markers?.decorations ?? []), ...marks]);
      editor.changeViewZones((accessor) => {
        for (const id of diffZones) accessor.removeZone(id);
        diffZones = markers === null ? [] : addDiffZones(editor, accessor, markers);
      });
      const notices: (string | false)[] = [
        deleted && "Deleted since this walkthrough recorded it",
        changes === null && "Too large to compare",
        file.late === true && "Recorded after edits to this file began",
        afterText() !== null &&
          live() !== afterText() &&
          "Changed since this walkthrough was updated",
        lost.length > 0 && "A noted line changed",
      ];
      options.onNotices(notices.filter((notice): notice is string => notice !== false));
    });

    // Monaco draws a zone only when the position after it is visible. Under a line whose next ones are hidden, a
    // card goes after the hidden lines, which take no room, when a shown line follows them; under the block's last
    // shown lines it hangs after the last character of the nearest line above that has one.
    const hang = (line: number): { afterLine: number; afterColumn?: number } => {
      let next = line + 1;
      while (next <= model.getLineCount() && !isShown(next)) next++;
      if (next <= model.getLineCount()) return { afterLine: next - 1 };
      for (let above = line; above >= shown().first; above--) {
        const column = model.getLineMaxColumn(above) - 1;
        if (isShown(above) && column >= 1) return { afterLine: above, afterColumn: column };
      }
      return { afterLine: line };
    };
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
        ...(code.folds ?? []).map(
          (fold): LineCard => ({
            kind: "fold",
            key: `fold:${fold.from}`,
            line: fold.from - 1,
            from: fold.from,
            to: fold.to,
            label: fold.label,
            open: openFolds().has(fold.from),
          }),
        ),
        ...questions().map((question): LineCard => ({ kind: "question", ...question })),
      ];
      return list.map((card) => ({ key: card.key, ...hang(at(card.line).afterLine), data: card }));
    });
    const removeCards = createZoneCards(
      editor,
      cards,
      (card) => <Card card={card()} toggleFold={options.toggleFold} />,
      0,
    );

    const measure = (): void => {
      mount.style.height = `${editor.getContentHeight()}px`;
    };
    const sized = editor.onDidContentSizeChange(measure);
    measure();
    // Asking is from a selection, once its gesture completes; the copy's line keeps the question in place.
    const unwatch = watchSelectionGestures(editor, options.actions, (selection, point) => {
      // The card and its mark sit under the selection's last line, so the quote is that line's text.
      const last = lastLineOf(selection);
      const quote = model
        .getValueInRange(
          selection.intersectRanges(new monaco.Range(last, 1, last, model.getLineMaxColumn(last)))!,
        )
        .trim()
        .slice(0, 200);
      if (map() === null || quote === "") {
        options.actions.clear();
        return;
      }
      const line = deleted ? last : mapLine(diffLines(live(), copy), last).afterLine;
      options.onSelect(selectionExcerpt(model, selection).text, quote, line, point);
    });

    return () => {
      clearTimeout(timer);
      changed.dispose();
      sized.dispose();
      unwatch();
      removeCards();
      dispose();
      editor.dispose();
      widgets.remove();
    };
  });
}

function Card(props: { card: LineCard; toggleFold: (from: number) => void }): JSX.Element {
  return (
    <Switch>
      <Match when={props.card.kind === "question" && props.card}>{(card) => card().render()}</Match>
      <Match when={props.card.kind === "fold" && props.card}>
        {(fold) => (
          <button
            type="button"
            class="ui-row walkthrough-fold"
            title={fold().open ? "Hide These Lines" : "Show These Lines"}
            onClick={() => props.toggleFold(fold().from)}
          >
            <span class="ui-row-name">
              {fold().open ? "Hide" : `${fold().to - fold().from + 1} lines`} · {fold().label}
            </span>
          </button>
        )}
      </Match>
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
