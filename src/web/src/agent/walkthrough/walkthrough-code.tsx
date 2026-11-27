// One code block of a walkthrough: a read-only editor on the live file, showing the block's lines as the walkthrough
// recorded them, mapped onto the file now, with the diff from the walkthrough's first copy painted in. Notes, closed
// folds and questions are cards between the lines, so they take focus and keyboard input as inline replies do.

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
import type { ReviewCopyScope } from "../../editor/editor-host";
import { hiddenAreas } from "../../editor/hidden-areas";
import { diffLines, type LineChanges, mapLine } from "../../editor/line-anchor";
import { createEmbeddedEditor, monaco } from "../../editor/monaco-setup";
import { computeTextDiffLines } from "../../editor/review/diff-computation";
import { computeDiffMarkers } from "../../editor/review/diff-markers";
import { addDiffZones, DIFF_RECOMPUTE_DEBOUNCE_MS } from "../../editor/review/diff-zones";
import { createZoneCards, type ZoneCard } from "../../editor/zone-cards";

const HIDDEN_AREAS_SOURCE = "weavie.walkthrough";

/** A card between a block's lines; `line` is numbered in the block's recorded copy. */
export type WalkthroughLineCard =
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
  | { kind: "question"; key: string; line: number; render: () => JSX.Element };

export interface WalkthroughCodeOptions {
  scope: ReviewCopyScope;
  /** The worktree `code.path` is relative to. */
  root: string;
  mount: HTMLElement;
  code: WalkthroughCode;
  file: WalkthroughFile;
  source: (hash: string) => Promise<string>;
  /** The folds the reader opened, by first line. */
  openFolds: Accessor<ReadonlySet<number>>;
  toggleFold: (from: number) => void;
  /** The questions asked about this block, under their line. */
  questions: Accessor<readonly Extract<WalkthroughLineCard, { kind: "question" }>[]>;
  /** The reader selected `text`, ending on `line` of the recorded copy, at a point on screen. */
  onSelect: (text: string, line: number, point: { x: number; y: number }) => void;
  onNotice: (notice: string | null) => void;
}

/** Builds the block's editor once its texts load; the returned function disposes it. */
export async function createWalkthroughCode(options: WalkthroughCodeOptions): Promise<() => void> {
  const { code, file, mount } = options;
  const [copy, before] = await Promise.all([
    options.source(code.source),
    options.source(file.before),
  ]);
  let model: monaco.editor.ITextModel;
  let snapshot: monaco.editor.ITextModel | null = null;
  try {
    model = (await options.scope.open(`${options.root}/${code.path}`, true)).model;
  } catch {
    snapshot = monaco.editor.createModel(
      copy,
      undefined,
      monaco.Uri.from({ scheme: "weavie-walkthrough", path: `/${code.path}`, query: code.source }),
    );
    model = snapshot;
  }
  const widgets = document.createElement("div");
  widgets.className = "monaco-editor walkthrough-overflow-widgets";
  document.body.append(widgets);
  const editor = createEmbeddedEditor(
    mount,
    model,
    {
      dimension: { width: mount.clientWidth, height: mount.clientHeight },
      overflowWidgetsDomNode: widgets,
    },
    {
      readOnly: true,
      domReadOnly: true,
      scrollBeyondLastLine: false,
      overviewRulerLanes: 0,
      overviewRulerBorder: false,
      hideCursorInOverviewRuler: true,
      minimap: { enabled: false },
      folding: false,
      glyphMargin: false,
      stickyScroll: { enabled: false },
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
    // The recorded copy mapped onto the file now; null when the file is too large to compare.
    const map = createMemo((): LineChanges | null => {
      try {
        return snapshot === null ? diffLines(copy, live()) : [];
      } catch {
        return null;
      }
    });
    const at = (line: number) => mapLine(map() ?? [], line);
    const copyLines = copy.split(/\r?\n/);
    const decorations = editor.createDecorationsCollection();
    let diffZones: string[] = [];
    // The text of the copy the last update recorded, once loaded.
    const [afterText, setAfterText] = createSignal<string | null>(null);
    if (file.after) void options.source(file.after).then(setAfterText);

    const paint = (): void => {
      const changes = map();
      const lineCount = model.getLineCount();
      const first = at(code.from).afterLine;
      const last = Math.max(first, at(code.to).afterLine);
      const hidden: monaco.IRange[] = [];
      if (first > 1) hidden.push(new monaco.Range(1, 1, first - 1, 1));
      if (last < lineCount) hidden.push(new monaco.Range(last + 1, 1, lineCount, 1));
      for (const fold of code.folds ?? []) {
        if (options.openFolds().has(fold.from)) continue;
        hidden.push(new monaco.Range(at(fold.from).afterLine, 1, at(fold.to).afterLine, 1));
      }
      hiddenAreas(editor).setHiddenAreas(hidden, HIDDEN_AREAS_SOURCE);
      const marks: monaco.editor.IModelDeltaDecoration[] = [];
      let lost = 0;
      for (const note of code.notes ?? []) {
        const anchor = at(note.line);
        const column = anchor.exact
          ? model.getLineContent(anchor.afterLine).indexOf(note.token)
          : -1;
        if (column < 0) {
          lost++;
          continue;
        }
        marks.push({
          range: new monaco.Range(
            anchor.afterLine,
            column + 1,
            anchor.afterLine,
            column + 1 + note.token.length,
          ),
          options: { inlineClassName: "walkthrough-mark" },
        });
      }
      const diff =
        snapshot === null && changes !== null ? computeTextDiffLines(before, live()) : null;
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
      options.onNotice(
        snapshot !== null
          ? "Deleted since this walkthrough recorded it"
          : changes === null
            ? "Too large to compare"
            : file.late
              ? "Recorded after edits to this file began"
              : afterText() !== null && live() !== afterText()
                ? "Changed since this walkthrough was updated"
                : lost > 0
                  ? "A noted line changed"
                  : null,
      );
    };
    createMemo(paint);

    const cards = createMemo((): ZoneCard<WalkthroughLineCard>[] => {
      map();
      const list: WalkthroughLineCard[] = [
        ...(code.notes ?? []).flatMap((note): WalkthroughLineCard[] =>
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
          (fold): WalkthroughLineCard => ({
            kind: "fold",
            key: `fold:${fold.from}`,
            line: fold.from - 1,
            from: fold.from,
            to: fold.to,
            label: fold.label,
            open: options.openFolds().has(fold.from),
          }),
        ),
        ...options.questions(),
      ];
      // Monaco hides a zone whose next line is hidden, so one above a closed fold goes after its last line.
      const place = (line: number): number => {
        const after = at(line).afterLine;
        const fold = (code.folds ?? []).find(
          (candidate) =>
            !options.openFolds().has(candidate.from) && at(candidate.from).afterLine === after + 1,
        );
        return fold === undefined ? after : at(fold.to).afterLine;
      };
      return list.map((card) => ({ key: card.key, afterLine: place(card.line), data: card }));
    });
    const removeCards = createZoneCards(
      editor,
      cards,
      (card) => <LineCard card={card()} toggleFold={options.toggleFold} />,
      0,
    );

    const measure = (): void => {
      mount.style.height = `${editor.getContentHeight()}px`;
    };
    const sized = editor.onDidContentSizeChange(measure);
    measure();
    // Asking is from a selection; the copy's line keeps the question in place as the file changes.
    const selected = editor.onDidChangeCursorSelection((event) => {
      const selection = event.selection;
      if (selection.isEmpty()) return;
      const text = model.getValueInRange(selection);
      const end = editor.getScrolledVisiblePosition(selection.getEndPosition());
      const box = editor.getDomNode()?.getBoundingClientRect();
      if (end === null || box === undefined || text.trim().length === 0) return;
      let line = selection.endLineNumber;
      try {
        line = mapLine(diffLines(live(), copy), selection.endLineNumber).afterLine;
      } catch {}
      options.onSelect(text, line, { x: box.left + end.left, y: box.top + end.top + end.height });
    });

    return () => {
      clearTimeout(timer);
      for (const disposable of [changed, sized, selected]) disposable.dispose();
      removeCards();
      dispose();
      editor.dispose();
      widgets.remove();
      snapshot?.dispose();
    };
  });
}

function LineCard(props: {
  card: WalkthroughLineCard;
  toggleFold: (from: number) => void;
}): JSX.Element {
  return (
    <Switch>
      <Match when={props.card.kind === "question" && props.card}>{(card) => card().render()}</Match>
      <Match when={props.card.kind === "fold" && props.card}>
        {(fold) => (
          <button
            type="button"
            class="walkthrough-fold"
            title={fold().open ? "Hide These Lines" : "Show These Lines"}
            onClick={() => props.toggleFold(fold().from)}
          >
            {fold().open ? "Hide" : `${fold().to - fold().from + 1} lines`} · {fold().label}
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
