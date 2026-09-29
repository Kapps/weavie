import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { equals } from "@codingame/monaco-vscode-api/vscode/vs/base/common/objects";
import type { TextModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/textModel";
import { ITextResourceConfigurationService } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/services/textResourceConfiguration.service";
import { batch, createEffect, createSignal, type JSX, onCleanup, Show, untrack } from "solid-js";
import { registerMiddleClickScroll } from "../../chrome/middle-click-scroll-surface";
import { onEditorOptionsChanged } from "../../editor-options";
import { onFontsChanged } from "../../fonts";
import type { ReviewCopy } from "../editor-host";
import type { ReviewFileBodyProps } from "./ReviewFileBody";
import type { ReviewCommentLayout } from "./review-comment-layout";
import type { ReviewDocument } from "./review-document";
import { reviewEditorConfiguration } from "./review-editor-configuration";
import { createReviewHorizontalScroll } from "./review-horizontal-scroll";
import { buildReviewHunkControls } from "./review-hunk-controls";
import type { PassiveCopyMap } from "./review-passive-copy";
import { preparePassiveDocument } from "./review-passive-document";
import { type PassiveChunk, renderPassiveChunks } from "./review-passive-lines";
import type {
  PassiveReviewPresentation,
  PreparedPassiveReview,
} from "./review-passive-presentation";
import { captureReviewDecorations, sameReviewDecorations } from "./review-projection-decorations";
import type { ReviewSectionFailure } from "./review-section";
import { hasReviewChanges } from "./review-store";
import type { ReviewToolbarTarget } from "./review-toolbar-state";
import { routeReviewWheel } from "./review-wheel";

/** Prepared paint is retained independently of the interactive editor. */
export function ReviewPassiveBody(
  props: Pick<
    ReviewFileBodyProps,
    | "file"
    | "scroller"
    | "openCopy"
    | "documents"
    | "preparePassive"
    | "onEditorHeight"
    | "measure"
    | "onEditor"
    | "header"
    | "horizontal"
  > & {
    comments: ReviewCommentLayout;
    target(): ReviewToolbarTarget;
    cursorLine(): number;
    onPresentation(value: PassiveReviewPresentation | undefined): void;
  },
): JSX.Element {
  let container!: HTMLDivElement;
  const [error, setError] = createSignal("");
  const [failure, setFailure] = createSignal<ReviewSectionFailure>();
  const [ready, setReady] = createSignal(false);
  const [available, setAvailable] = createSignal<PreparedPassiveReview>();
  const [controlsRevision, setControlsRevision] = createSignal(0);
  let cached: PreparedPassiveReview | undefined;
  let displayed: PreparedPassiveReview | undefined;
  let enabled = true;
  let decorationsDirty = false;
  let decorationCheckQueued = false;
  let copy: ReviewCopy | undefined;
  let reviewDocument: ReviewDocument | undefined;
  let configuration: ReturnType<typeof reviewEditorConfiguration> | undefined;
  let preparation: AbortController | undefined;
  let subscriptions: { dispose(): void }[] = [];
  let chunks: PassiveChunk[] = [];
  let copyMap: PassiveCopyMap | undefined;
  let visible = new Set<PassiveChunk>();
  let firstVisible = -1;
  let endVisible = -1;
  let containerTop = 0;
  let viewportHeight = 0;
  let width = 0;
  let dropped = false;
  let preparing = false;
  let rail: ReturnType<typeof createReviewHorizontalScroll> | undefined;
  let textLayers: HTMLElement[] = [];
  let lineHeight = 0;
  let headerHeight = 0;
  let visualRevision = 0;
  const controlsChanged = props.documents.onDidChangeConfiguration((uri) => {
    if (copy?.model.uri.toString() === uri) setControlsRevision((value) => value + 1);
  });
  onCleanup(() => controlsChanged.dispose());
  createEffect(() => {
    const prepared = available();
    controlsRevision();
    const target = untrack(props.target);
    if (!prepared || target.kind !== "file") return;
    const controls = buildReviewHunkControls(
      target.document,
      target.presentation,
      prepared.source.markers,
      (button) => button,
    );
    for (const control of controls) {
      const position = prepared.rendered.controlPositions.get(control.line);
      if (!position) throw new Error("Missing passive hunk control position");
      control.element.style.cssText = `position:absolute;left:${position.left}px;top:${position.top}px;user-select:none`;
      position.host.append(control.element);
    }
    onCleanup(() => {
      for (const control of controls) control.element.remove();
    });
  });
  const publish = (value: PreparedPassiveReview | undefined): void => {
    batch(() => {
      setAvailable(value);
      setReady(value !== undefined);
    });
  };
  const invalidate = (): void => {
    cached = undefined;
    publish(undefined);
    visualRevision++;
    setReady(false);
    if (enabled && !preparing) void prepare();
  };
  const matches = (value: PreparedPassiveReview): boolean =>
    !dropped &&
    !value.source.model.isDisposed() &&
    value.source.version === value.source.model.getVersionId() &&
    value.source.language === value.source.model.getLanguageId() &&
    value.diff === untrack(() => props.file().diff());
  const current = (): PreparedPassiveReview | undefined => {
    if (dropped || !cached) return undefined;
    if (!matches(cached)) {
      invalidate();
      return undefined;
    }
    if (decorationsDirty) {
      decorationsDirty = false;
      if (
        !sameReviewDecorations(cached.decorations, captureReviewDecorations(cached.source.model))
      ) {
        invalidate();
        return undefined;
      }
    }
    publish(cached);
    return cached;
  };
  const decorationsChanged = (): void => {
    decorationsDirty = true;
    publish(undefined);
    if (!enabled || decorationCheckQueued) return;
    decorationCheckQueued = true;
    queueMicrotask(() => {
      decorationCheckQueued = false;
      if (dropped || !enabled) return;
      if (!current() && !preparing) void prepare();
    });
  };
  const sync = (): void => {
    if (!enabled) return;
    const top = props.scroller().getScrollTop() - containerTop;
    rail?.place(top + headerHeight, Math.min(top + viewportHeight, cached?.rendered.height ?? 0));
    let first = 0;
    while (first < chunks.length && chunks[first]!.top + chunks[first]!.height <= top) first++;
    let end = first;
    while (end < chunks.length && chunks[end]!.top < top + viewportHeight) end++;
    if (first === firstVisible && end === endVisible) return;
    firstVisible = first;
    endVisible = end;
    const next = new Set(chunks.slice(first, end));
    const selection = document.getSelection();
    for (const chunk of visible) {
      if (next.has(chunk)) continue;
      if (
        chunk.node.contains(document.activeElement) ||
        (selection && !selection.isCollapsed && selection.containsNode(chunk.node, true))
      ) {
        next.add(chunk);
      } else chunk.node.remove();
    }
    const ordered = [...next].sort((a, b) => a.top - b.top);
    for (let index = ordered.length - 1; index >= 0; index--) {
      const chunk = ordered[index]!;
      if (!visible.has(chunk)) container.insertBefore(chunk.node, ordered[index + 1]?.node ?? null);
    }
    visible = next;
  };
  const layout = (): void => {
    if (!container?.isConnected) return;
    const viewport = props.scroller().viewport;
    const bounds = container.getBoundingClientRect();
    containerTop =
      bounds.top - viewport.getBoundingClientRect().top + props.scroller().getScrollTop();
    viewportHeight = viewport.clientHeight;
    headerHeight = props.header().getBoundingClientRect().height;
    if (width !== bounds.width) {
      width = bounds.width;
      invalidate();
    }
    sync();
  };
  const prepare = async (): Promise<void> => {
    const diff = untrack(() => props.file().diff());
    if (!diff || !hasReviewChanges(diff) || !width || dropped || !enabled) return;
    preparation?.abort();
    const operation = new AbortController();
    const requestedRevision = visualRevision;
    preparation = operation;
    preparing = true;
    cached = undefined;
    publish(undefined);
    let paintedRevision: number | undefined;
    let prepared: PreparedPassiveReview | undefined;
    setReady(false);
    try {
      await props.preparePassive(
        async () => {
          operation.signal.throwIfAborted();
          const value = await props.openCopy();
          operation.signal.throwIfAborted();
          if (copy?.model !== value.model) {
            for (const subscription of subscriptions) subscription.dispose();
            copy = value;
            configuration = reviewEditorConfiguration(value.model, { readOnly: !value.editable });
            subscriptions = [
              value.model.onDidChangeContent(() => invalidate()),
              (value.model as TextModel).onDidChangeTokens(() => invalidate()),
              value.model.onDidChangeLanguage(() => invalidate()),
              value.model.onDidChangeOptions(() => invalidate()),
              value.model.onDidChangeDecorations(decorationsChanged),
              value.model.onWillDispose(() => invalidate()),
              StandaloneServices.get(ITextResourceConfigurationService).onDidChangeConfiguration(
                (event) => {
                  if (!event.affectsConfiguration(value.model.uri, "editor")) return;
                  const next = reviewEditorConfiguration(value.model, {
                    readOnly: !value.editable,
                  });
                  if (equals(configuration, next)) return;
                  configuration = next;
                  invalidate();
                },
              ),
            ];
          }
          reviewDocument = props.documents.forModel(copy.model);
          const documentModel = await preparePassiveDocument(
            reviewDocument,
            diff,
            operation.signal,
          );
          return { documentModel, copy: value, revision: visualRevision };
        },
        ({ documentModel, copy, revision }) => {
          operation.signal.throwIfAborted();
          if (dropped || preparation !== operation || untrack(() => props.file().diff()) !== diff)
            return;
          if (
            revision !== visualRevision ||
            documentModel.model.isDisposed() ||
            documentModel.model.getVersionId() !== documentModel.version ||
            documentModel.model.getLanguageId() !== documentModel.language
          )
            return;
          const decorations = captureReviewDecorations(documentModel.model);
          const rendered = renderPassiveChunks(
            documentModel,
            width,
            { readOnly: !copy.editable },
            props.cursorLine(),
            props.comments.geometry(),
          );
          props.comments.place(rendered.commentTops, rendered.layout.contentLeft);
          textLayers = rendered.textLayers;
          lineHeight = rendered.lineHeight;
          rail!.configure(
            rendered.layout.contentLeft,
            rendered.layout.contentWidth,
            rendered.contentWidth,
            rendered.configuration.scrollbar!.horizontalScrollbarSize!,
          );
          for (const layer of textLayers) layer.style.left = `${-props.horizontal.get()}px`;
          configuration = rendered.configuration;
          if (operation.signal.aborted || preparation !== operation) return;
          for (const chunk of visible) chunk.node.remove();
          visible.clear();
          firstVisible = endVisible = -1;
          chunks = rendered.chunks;
          copyMap = rendered.copy;
          for (const chunk of chunks) {
            chunk.node.style.position = "absolute";
            chunk.node.style.top = `${chunk.top}px`;
          }
          container.style.height = `${rendered.height}px`;
          paintedRevision = revision;
          setError("");
          setFailure(undefined);
          if (props.onEditorHeight(rendered.height)) props.measure();
          layout();
          prepared = {
            copy,
            document: props.documents.forModel(copy.model),
            source: documentModel,
            diff,
            rendered,
            width,
            decorations,
          };
        },
      );
    } catch (cause) {
      if (operation.signal.aborted || dropped || preparation !== operation) return;
      paintedRevision = undefined;
      if (visualRevision === requestedRevision) {
        setError(`${String(cause)} — select this file again to retry.`);
        setFailure({
          error: cause,
          retry: () => {
            setError("");
            setFailure(undefined);
            resume();
          },
        });
      }
    } finally {
      if (preparation === operation) {
        preparing = false;
        if (visualRevision !== (paintedRevision ?? requestedRevision)) {
          queueMicrotask(() => void prepare());
        } else if (paintedRevision !== undefined && prepared && !operation.signal.aborted) {
          cached = prepared;
          displayed = prepared;
          current();
          sync();
        }
      }
    }
  };
  const resume = (): void => {
    if (dropped) return;
    enabled = true;
    if (!current() && !preparing && !failure()) void prepare();
    sync();
  };
  createEffect(() => {
    props.comments.geometry();
    props.cursorLine();
    invalidate();
  });
  createEffect(() => {
    const diff = props.file().diff();
    if (!diff || !hasReviewChanges(diff)) {
      cached = undefined;
      displayed = undefined;
      publish(undefined);
      preparation?.abort();
      preparation = undefined;
      preparing = false;
      for (const subscription of subscriptions) subscription.dispose();
      subscriptions = [];
      copy = undefined;
      reviewDocument = undefined;
      for (const chunk of visible) chunk.node.remove();
      chunks = [];
      copyMap = undefined;
      visible.clear();
      firstVisible = endVisible = -1;
      container.style.height = "0px";
      setReady(props.file().loaded());
      setError("");
      setFailure(undefined);
      if (props.onEditorHeight(0)) props.measure();
      return;
    }
    invalidate();
  });
  createEffect(() => {
    if (!container) return;
    const scroller = props.scroller();
    rail = createReviewHorizontalScroll(container, props.horizontal);
    onCleanup(() => {
      rail?.dispose();
      rail = undefined;
    });
    onCleanup(
      props.horizontal.subscribe(() => {
        for (const layer of textLayers) layer.style.left = `${-props.horizontal.get()}px`;
      }),
    );
    onCleanup(
      registerMiddleClickScroll(container, () => true, {
        x: (delta) => rail!.move(delta),
        y: null,
      }),
    );
    onCleanup(scroller.onScroll(sync));
    const resize = new ResizeObserver(layout);
    resize.observe(container);
    resize.observe(scroller.viewport);
    const geometry = {
      layout,
      shift: (delta: number) => {
        if (dropped) return;
        containerTop += delta;
        sync();
      },
    };
    props.onEditor(geometry);
    props.onPresentation({
      ...geometry,
      prepared: () => {
        const value = available();
        return value && matches(value) ? value : undefined;
      },
      current,
      displayed: () => displayed,
      document: () => (copy && !copy.model.isDisposed() ? reviewDocument : undefined),
      error,
      failure,
      bounds: () => {
        const top = scroller.getScrollTop() + headerHeight - containerTop;
        return {
          top,
          bottom: Math.min(top + viewportHeight - headerHeight, cached?.rendered.height ?? 0),
          height: viewportHeight - headerHeight,
        };
      },
      reveal: (top) => scroller.setScrollTop(containerTop - headerHeight + top),
      suspend: () => {
        enabled = false;
        preparation?.abort();
        preparation = undefined;
        preparing = false;
      },
      resume,
    });
    layout();
    onCleanup(() => {
      resize.disconnect();
      props.onEditor(undefined);
      props.onPresentation(undefined);
    });
  });
  onCleanup(onFontsChanged(() => invalidate()));
  onCleanup(onEditorOptionsChanged(() => invalidate()));
  onCleanup(() => {
    dropped = true;
    cached = undefined;
    setAvailable(undefined);
    preparation?.abort();
    for (const subscription of subscriptions) subscription.dispose();
    chunks = [];
    copyMap = undefined;
    visible.clear();
  });
  return (
    <>
      <Show when={error()}>
        <div class="unified-review-notice">{error()}</div>
      </Show>
      <Show when={!ready() && !error()}>
        <div class="unified-review-notice">Preparing review…</div>
      </Show>
      <div
        class="unified-review-editor passive-review-body"
        ref={container}
        onWheel={(event) =>
          routeReviewWheel(event, props.scroller(), lineHeight, width, (delta) => rail?.move(delta))
        }
        onCopy={(event) => {
          const selection = document.getSelection();
          if (
            !selection?.anchorNode ||
            !selection.focusNode ||
            !container.contains(selection.anchorNode) ||
            !container.contains(selection.focusNode)
          )
            return;
          const text = selection && copyMap?.copy(selection);
          if (text !== undefined && text !== null) {
            event.clipboardData?.setData("text/plain", text);
            event.preventDefault();
          }
        }}
      />
    </>
  );
}
