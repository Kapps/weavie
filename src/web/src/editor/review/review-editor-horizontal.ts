import {
  DisposableStore,
  dispose,
  toDisposable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import type { TextModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import { monaco } from "../monaco-setup";
import type { ReviewHorizontalPosition } from "./review-horizontal-position";
import { reserveReviewLineWidth } from "./review-line-width";
import { captureReviewDecorations, sameReviewDecorations } from "./review-projection-decorations";

type WidthEditor = {
  _modelData: { viewModel: { viewLayout: Parameters<typeof reserveReviewLineWidth>[0] } };
};

/** A live binding owns the file's range; prepared width survives its first visible render band. */
export function createReviewEditorHorizontal(
  editor: monaco.editor.IStandaloneCodeEditor,
  update: (change: () => void) => void,
  position: ReviewHorizontalPosition,
  prepared: { minimumContentWidth: number; viewportWrapping: boolean },
) {
  const subscriptions = new DisposableStore();
  const range = position.bindRange();
  let reservation: ReturnType<typeof reserveReviewLineWidth> | undefined;
  let zone: string | undefined;
  let reserved = true;
  let ready = false;
  let applying = false;
  let disposed = false;
  const maximum = (): number => editor.getScrollWidth() - editor.getLayoutInfo().contentWidth;
  const removeReservation = (): void => {
    if (zone !== undefined) {
      update(() => editor.changeViewZones((zones) => zones.removeZone(zone!)));
      zone = undefined;
    }
    reservation?.clear();
  };
  const clear = (): void => {
    if (!reserved || disposed) return;
    reserved = false;
    removeReservation();
  };
  const release = (): void => {
    if (disposed) return;
    disposed = true;
    dispose([
      subscriptions,
      toDisposable(removeReservation),
      toDisposable(() => reservation?.dispose()),
      range,
    ]);
  };
  const restore = (change: () => void): void => {
    if (disposed) return;
    applying = true;
    try {
      update(change);
      editor.render(true);
      range.update(maximum());
      editor.setScrollLeft(position.get(), monaco.editor.ScrollType.Immediate);
      editor.render(true);
      range.update(maximum());
      if (editor.getScrollLeft() !== position.get())
        throw new Error("The review horizontal position does not match its measured range");
      ready = true;
    } finally {
      applying = false;
    }
  };
  try {
    if (prepared.viewportWrapping) {
      reservation = reserveReviewLineWidth(
        (editor as unknown as WidthEditor)._modelData.viewModel.viewLayout,
        prepared.minimumContentWidth,
      );
    } else {
      update(() =>
        editor.changeViewZones((zones) => {
          zone = zones.addZone({
            afterLineNumber: 0,
            heightInPx: 0,
            minWidthInPx: prepared.minimumContentWidth,
            domNode: document.createElement("div"),
          });
        }),
      );
    }
    const model = editor.getModel()! as TextModel;
    const decorations = captureReviewDecorations(model);
    const configuredOptions = Object.values(monaco.editor.EditorOption).filter(
      (value): value is monaco.editor.EditorOption =>
        typeof value === "number" && value !== monaco.editor.EditorOption.layoutInfo,
    );
    subscriptions.add(editor.onDidChangeModelContent(clear));
    subscriptions.add(editor.onDidChangeModelOptions(clear));
    subscriptions.add(model.onDidChangeLanguage(clear));
    subscriptions.add(model.onDidChangeTokens(clear));
    subscriptions.add(
      model.onDidChangeDecorations(() => {
        if (reserved && !sameReviewDecorations(decorations, captureReviewDecorations(model)))
          clear();
      }),
    );
    subscriptions.add(
      editor.onDidChangeConfiguration((event) => {
        // Wrapping changes invalidate glyph widths; resizing an unchanged projection does not.
        if (configuredOptions.some((option) => event.hasChanged(option))) clear();
      }),
    );
    const acceptScroll = (leftChanged: boolean): void => {
      if (!ready || applying || disposed) return;
      applying = true;
      try {
        range.update(maximum());
        if (leftChanged) position.set(editor.getScrollLeft());
      } finally {
        applying = false;
      }
    };
    subscriptions.add(
      editor.onDidScrollChange((event) => {
        if (event.scrollLeftChanged || event.scrollWidthChanged)
          acceptScroll(event.scrollLeftChanged);
      }),
    );
    subscriptions.add(editor.onDidLayoutChange(() => acceptScroll(true)));
    subscriptions.add(
      toDisposable(
        position.subscribe(() => {
          if (!applying && ready) restore(() => {});
        }),
      ),
    );
    return { restore, dispose: release };
  } catch (error) {
    try {
      release();
    } catch (cleanup) {
      throw new AggregateError([error, cleanup], "Review horizontal binding and cleanup failed");
    }
    throw error;
  }
}
