import {
  dispose,
  toDisposable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import {
  batch,
  createEffect,
  createSignal,
  type JSX,
  onCleanup,
  onMount,
  Show,
  untrack,
} from "solid-js";
import type { monaco } from "../monaco-setup";
import type { ReviewFileBodyProps } from "./ReviewFileBody";
import { ReviewPassiveBody } from "./ReviewPassiveBody";
import { createAdaptiveReviewSection } from "./review-adaptive-section";
import { createReviewCommentLayout } from "./review-comment-layout";
import { createReviewEditor, type ReviewEditor } from "./review-editor";
import type {
  PassiveReviewPresentation,
  PreparedPassiveReview,
} from "./review-passive-presentation";
import type { ReviewSectionFailure } from "./review-section";
import { hasReviewChanges } from "./review-store";
import type { ReviewSectionBinding } from "./review-surface";

/** File-owned presentation: scrolling never activates or retires an editor. */
export function ReviewAdaptiveBody(props: ReviewFileBodyProps): JSX.Element {
  const path = props.file().summary().path;
  let root!: HTMLElement;
  let liveHost!: HTMLDivElement;
  const [passive, setPassive] = createSignal<PassiveReviewPresentation>();
  const [live, setLive] = createSignal<ReviewEditor>();
  const [error, setError] = createSignal("");
  const [failure, setFailure] = createSignal<ReviewSectionFailure>();
  const [cursorLine, setCursorLine] = createSignal(1);
  let saved:
    | { model: monaco.editor.ITextModel; state: monaco.editor.ICodeEditorViewState }
    | undefined;
  let binding: PreparedPassiveReview | undefined;
  let passiveHeight = props.editorHeight();
  let liveHeight = passiveHeight;
  let constructing = false;
  let releasing = false;
  let lifetime = 0;
  let retirementQueued = false;
  let disposed = false;
  let pointerId = 0;
  let registration: ReviewSectionBinding | undefined;
  const commentHost = document.createElement("div");
  commentHost.className = "review-file-comments";
  const comments = createReviewCommentLayout(commentHost, () => {
    live();
    passive()?.prepared();
    const model = binding?.copy.model ?? passive()?.displayed()?.copy.model;
    return model && !model.isDisposed() ? model : null;
  });
  const size = (height: number): void => {
    root.style.height = `${height}px`;
    if (props.onEditorHeight(height)) props.measure();
  };
  const present = (): void => {
    const editor = untrack(live);
    const prepared = untrack(() => passive()?.prepared());
    root.dataset.presentation = editor ? "live" : prepared ? "passive" : "pending";
  };
  const report = (cause: unknown): void => {
    setError(`${String(cause)} — select this file again to retry.`);
    setFailure({
      error: cause,
      retry: () => {
        setError("");
        setFailure(undefined);
        passive()?.resume();
        publish();
      },
    });
  };
  const publish = (): void => {
    if (disposed) return;
    registration?.changed();
  };
  const release = (): void => {
    lifetime++;
    const editor = untrack(live);
    if (!editor) return;
    const model = binding?.copy.model;
    binding = undefined;
    releasing = true;
    try {
      dispose([
        toDisposable(() => setLive(undefined)),
        toDisposable(() => {
          const state = editor.viewState();
          if (state && model) saved = { model, state };
        }),
        editor,
        toDisposable(() => liveHost.replaceChildren()),
      ]);
    } finally {
      releasing = false;
      if (!disposed) present();
    }
  };
  const activate = (): ReviewEditor | undefined => {
    if (disposed || constructing || releasing || props.file().collapsed()) return undefined;
    const existing = untrack(live);
    if (existing) return existing;
    const presentation = untrack(passive);
    if (!presentation) return undefined;
    presentation.layout();
    const prepared = presentation.current();
    if (!prepared) return undefined;
    const generation = lifetime;
    constructing = true;
    presentation.suspend();
    const mount = document.createElement("div");
    mount.className = "unified-review-editor";
    mount.style.height = `${prepared.rendered.height}px`;
    liveHost.append(mount);
    liveHeight = prepared.rendered.height;
    let candidate: ReviewEditor | undefined;
    try {
      candidate = createReviewEditor({
        session: props.session,
        tab: props.tab,
        scope: props.scope,
        container: mount,
        scroller: props.scroller(),
        header: props.header(),
        model: prepared.copy.model,
        editable: prepared.copy.editable,
        documents: props.documents,
        comments,
        horizontal: props.horizontal,
        preparedWidth: {
          minimumContentWidth: prepared.rendered.minimumContentWidth,
          viewportWrapping: prepared.rendered.viewportWrapping,
        },
        path,
        onHeight: (height) => {
          liveHeight = height;
          if (!constructing) size(height);
        },
        onChanged: publish,
        onCursor: (line) => {
          setCursorLine(line);
          if (!constructing) props.onCursor(line);
        },
      });
      if (!candidate.ready() || liveHeight !== prepared.rendered.height)
        throw new Error(
          "Prepared review activation changed its geometry or required asynchronous painting",
        );
      if (saved?.model === prepared.copy.model) candidate.restoreViewState(saved.state);
      else candidate.selectLine(cursorLine());
      const current = presentation.current();
      if (
        disposed ||
        generation !== lifetime ||
        untrack(passive) !== presentation ||
        props.file().collapsed() ||
        current !== prepared
      ) {
        dispose([candidate, toDisposable(() => mount.remove())]);
        candidate = undefined;
        if (!disposed) presentation.resume();
        return undefined;
      }
      binding = prepared;
      batch(() => {
        setError("");
        setFailure(undefined);
        setLive(candidate);
        present();
      });
      return !disposed && untrack(live) === candidate ? candidate : undefined;
    } catch (cause) {
      try {
        if (candidate && untrack(live) === candidate) release();
        else {
          binding = undefined;
          dispose([toDisposable(() => candidate?.dispose()), toDisposable(() => mount.remove())]);
        }
      } catch (cleanup) {
        throw new AggregateError([cause, cleanup], "Review activation and cleanup failed");
      } finally {
        if (!disposed) presentation.resume();
      }
      throw cause;
    } finally {
      constructing = false;
    }
  };
  const retire = (): void => {
    if (retirementQueued) return;
    retirementQueued = true;
    queueMicrotask(() => {
      retirementQueued = false;
      const editor = untrack(live);
      const presentation = untrack(passive);
      if (
        disposed ||
        constructing ||
        releasing ||
        !editor ||
        !presentation ||
        props.ownsEditor() ||
        editor.retained()
      )
        return;
      try {
        const collapsed = props.file().collapsed();
        if (!collapsed) {
          presentation.resume();
          if (!presentation.current()) return;
        }
        if (
          disposed ||
          untrack(live) !== editor ||
          untrack(passive) !== presentation ||
          props.ownsEditor() ||
          editor.retained()
        )
          return;
        release();
        // Teardown can remove global tracking decorations; only publish a revalidated projection.
        if (!collapsed && presentation.current()) size(passiveHeight);
        publish();
      } catch (cause) {
        report(cause);
      }
    });
  };
  const section = createAdaptiveReviewSection({
    path,
    scope: props.scope,
    valid: () => !disposed,
    collapsed: () => props.file().collapsed(),
    empty: () => {
      const file = props.file();
      const diff = file.diff();
      return file.loaded() && (!diff || !hasReviewChanges(diff));
    },
    failure,
    passive,
    editor: live,
    activate: () => {
      try {
        const editor = activate();
        if (editor) props.claimEditor();
        return editor;
      } catch (cause) {
        report(cause);
        throw cause;
      }
    },
    cursor: cursorLine,
    select: (line) => {
      if (saved?.state.cursorState[0]?.position.lineNumber !== line) saved = undefined;
      setCursorLine(line);
    },
    viewState: () =>
      saved?.model === passive()?.displayed()?.copy.model ? (saved?.state ?? null) : null,
    comments,
  });
  const configureComments = (): void => {
    const document = binding?.document ?? passive()?.displayed()?.document;
    comments.presenter.configure(document?.actions.options?.commenting);
  };
  createEffect(() => {
    props.file().comments();
    passive()?.prepared();
    live();
    untrack(configureComments);
  });
  const commentConfiguration = props.documents.onDidChangeConfiguration(configureComments);
  onCleanup(() => commentConfiguration.dispose());
  onCleanup(comments.dispose);
  onCleanup(props.horizontal.subscribe(props.controlsChanged));
  createEffect(() => {
    const presentation = passive();
    const prepared = presentation?.prepared();
    const editor = live();
    const owns = props.ownsEditor();
    const retained = editor?.retained();
    const collapsed = props.file().collapsed();
    failure();
    presentation?.failure();
    const diff = props.file().diff();
    untrack(() => {
      if (!presentation || disposed || releasing) return;
      if (
        !diff ||
        !hasReviewChanges(diff) ||
        (binding && diff.currentExists !== binding.diff.currentExists)
      ) {
        release();
        if (!diff || !hasReviewChanges(diff)) size(0);
        presentation.resume();
        present();
        publish();
        return;
      }
      if (collapsed) {
        presentation.suspend();
        if (editor && !owns && !retained) retire();
      } else if (!editor) {
        if (prepared) size(prepared.rendered.height);
        presentation.resume();
      } else if (owns || retained) presentation.suspend();
      else retire();
      present();
      publish();
    });
  });
  onMount(() => {
    registration = props.register.bind(path, section);
    let cancelFocus: (() => void) | undefined;
    const events = new AbortController();
    const listenerOptions = { capture: true, signal: events.signal };
    root.addEventListener(
      "pointerdown",
      (event) => {
        pointerId = event.pointerId;
      },
      listenerOptions,
    );
    root.addEventListener(
      "focusin",
      (event) => {
        if ((event.target as Element).closest(".review-file-comments")) return;
        if (event.target === root && !constructing) {
          event.stopPropagation();
          props.onCursor(cursorLine());
          cancelFocus?.();
          cancelFocus = props.requestFocus();
        } else if (!constructing && live()) props.claimEditor();
      },
      listenerOptions,
    );
    root.addEventListener(
      "focusout",
      (event) => {
        if (event.relatedTarget instanceof Node && root.contains(event.relatedTarget)) return;
        cancelFocus?.();
        cancelFocus = undefined;
      },
      listenerOptions,
    );
    root.addEventListener(
      "mousedown",
      (event) => {
        if (
          (event.target as Element).closest(
            ".review-horizontal-scroll, .review-file-comments, button",
          )
        )
          return;
        if (live()) {
          props.claimEditor();
          return;
        }
        if (event.button === 1) return;
        try {
          const editor = activate();
          if (!editor) {
            event.preventDefault();
            return;
          }
          editor.beginMouseDown(event, pointerId);
          props.claimEditor();
        } catch (cause) {
          report(cause);
        }
      },
      listenerOptions,
    );
    onCleanup(() => {
      events.abort();
      cancelFocus?.();
    });
    props.onEditor({
      layout: () => {
        passive()?.layout();
        live()?.layout();
      },
      shift: (delta) => {
        passive()?.shift(delta);
        live()?.shift(delta);
      },
    });
  });
  onCleanup(() => {
    disposed = true;
    registration?.dispose();
    release();
    props.onEditor(undefined);
  });
  return (
    <section
      class="review-adaptive-body"
      aria-label={`Changes in ${path}`}
      aria-busy={!live() && !passive()?.prepared()}
      tabIndex={live() ? -1 : 0}
      ref={(element) => {
        root = element;
        element.style.height = `${passiveHeight}px`;
      }}
      data-presentation="pending"
    >
      <div class="review-adaptive-passive" inert={live() !== undefined}>
        <ReviewPassiveBody
          {...props}
          cursorLine={cursorLine}
          comments={comments}
          target={section.passiveTarget}
          onPresentation={setPassive}
          onEditor={() => {}}
          onEditorHeight={(height) => {
            const changed = passiveHeight !== height;
            passiveHeight = height;
            if (!untrack(live) && !constructing) size(height);
            return changed;
          }}
          measure={() => {}}
        />
      </div>
      <div class="review-adaptive-live" ref={liveHost} />
      {commentHost}
      <Show when={error()}>
        <div class="unified-review-notice review-adaptive-notice">{error()}</div>
      </Show>
    </section>
  );
}
