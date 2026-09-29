import { IS_MAC } from "../../commands/keybindings";
import type { ReviewScopeState } from "../inline-diff";
import type { monaco } from "../monaco-setup";
import type { ReviewCommentLayout } from "./review-comment-layout";
import type { ReviewEditor } from "./review-editor";
import type { ReviewActionPresentation } from "./review-file-actions";
import type { PassiveReviewPresentation } from "./review-passive-presentation";
import type {
  ReviewSection,
  ReviewSectionFailure,
  ReviewSectionInput,
  ReviewSectionNavigation,
} from "./review-section";
import type { ReviewToolbarTarget } from "./review-toolbar-state";

/** Retained reading identity does not grant access to pending or hidden geometry. */
export function createAdaptiveReviewSection(options: {
  path: string;
  scope: ReviewScopeState;
  valid(): boolean;
  collapsed(): boolean;
  empty(): boolean;
  failure(): ReviewSectionFailure | undefined;
  passive(): PassiveReviewPresentation | undefined;
  editor(): ReviewEditor | undefined;
  activate(): ReviewEditor | undefined;
  cursor(): number;
  select(line: number): void;
  viewState(): monaco.editor.ICodeEditorViewState | null;
  comments: ReviewCommentLayout;
}): ReviewSection & { passiveTarget(): ReviewToolbarTarget } {
  const available = (): boolean => options.valid() && !options.collapsed() && !options.empty();
  const reviewLine = (): number => {
    const passive = options.passive();
    const displayed = passive?.displayed();
    if (!displayed) return options.cursor();
    const { top, bottom } = passive!.bounds();
    const geometry = displayed.rendered.geometry;
    const cursor = options.cursor();
    const cursorTop = geometry.topForLineNumber(cursor);
    return cursorTop >= Math.max(0, top) && cursorTop < bottom
      ? cursor
      : geometry.lineAtOffset((Math.max(0, top) + bottom) / 2);
  };
  const inputFor = (editor: ReviewEditor): ReviewSectionInput => {
    const current = (): boolean => available() && options.editor() === editor;
    return {
      current,
      focus: () => {
        if (current() && options.activate() === editor && current()) editor.focus();
      },
    };
  };
  const navigationFor = (editor: ReviewEditor): ReviewSectionNavigation | undefined => {
    const target = editor.target();
    if (target.kind !== "file" || target.paint.status !== "ready") return undefined;
    const paint = target.paint;
    const input = inputFor(editor);
    const current = (): boolean =>
      input.current() &&
      target.presentation.valid() &&
      !target.document.actions.stale &&
      target.document.actions.options === paint.options &&
      target.document.actions.geometry?.markers === paint.markers;
    if (!current()) return undefined;
    return {
      current,
      focus: () => {
        if (current()) input.focus();
      },
      restore: (location) => {
        if (current()) editor.restore(location);
      },
      revealFileStart: (line) => {
        if (current()) editor.revealFileStart(line);
      },
    };
  };
  const section: ReviewSection & { passiveTarget(): ReviewToolbarTarget } = {
    capture: () => {
      const editor = options.editor();
      if (editor) return editor.capture();
      const line = reviewLine();
      const passive = options.passive();
      const painted = passive?.displayed();
      return {
        path: options.path,
        line,
        viewState: options.viewState(),
        ...(painted
          ? {
              anchor: {
                line,
                offset: passive!.bounds().top - painted.rendered.geometry.topForLineNumber(line),
              },
            }
          : {}),
      };
    },
    state: () => {
      if (options.collapsed()) return { kind: "collapsed" };
      if (options.empty() || !options.valid()) return { kind: "empty" };
      const editor = options.editor();
      const input = editor && inputFor(editor);
      const target = editor?.target() ?? section.passiveTarget();
      const failure = options.failure() ?? (!editor ? options.passive()?.failure() : undefined);
      if (failure) return { kind: "unavailable", input, failure, target };
      if (editor) {
        if (
          target.kind === "file" &&
          target.paint.status === "unavailable" &&
          target.presentation.valid() &&
          !target.document.actions.stale &&
          target.document.actions.options === target.paint.options
        )
          return {
            kind: "unavailable",
            input,
            target,
            failure: { error: target.paint.message, retry: editor.retry },
          };
        const navigation = navigationFor(editor);
        return navigation
          ? {
              kind: "ready",
              input,
              target,
              enter: () =>
                navigation.current() && options.activate() === editor && navigation.current()
                  ? navigation
                  : undefined,
            }
          : { kind: "pending", input };
      }
      const passive = options.passive();
      const prepared = passive?.prepared();
      if (!prepared || target.kind !== "file" || !target.presentation.valid())
        return { kind: "pending", input };
      return {
        kind: "ready",
        input,
        target,
        enter: (intent) => {
          if (!target.presentation.valid()) return undefined;
          if (intent === "focus") {
            const { top, height } = passive!.bounds();
            if (top + height <= 0 || top >= prepared.rendered.height) passive!.reveal(0);
          }
          const editor = options.activate();
          return editor && navigationFor(editor);
        },
      };
    },
    passiveTarget: () => {
      const passive = options.passive();
      const prepared = passive?.prepared();
      const document = prepared?.document ?? passive?.document();
      const message = passive?.error();
      const configuration = document?.actions.options;
      if (!available() || !document || !configuration || (!prepared && !message))
        return { kind: "none" };
      const presentation: ReviewActionPresentation = {
        scope: options.scope,
        valid: () =>
          available() &&
          !options.editor() &&
          options.passive() === passive &&
          passive?.prepared() === prepared &&
          document.actions.options === configuration &&
          (prepared !== undefined ||
            (passive?.document() === document && passive?.error() === message)),
        availability: () => (prepared ? "ready" : "unavailable"),
        reviewLine,
        commentLine: reviewLine,
        revealLine: (line) => {
          if (!prepared || !presentation.valid()) return;
          options.select(line);
          passive!.reveal(
            prepared.rendered.geometry.topForLineNumber(line) -
              (passive!.bounds().height - prepared.rendered.lineHeight) / 2,
          );
        },
        selectLine: options.select,
        openComment: (line) => options.comments.presenter.open(line),
        composerFocused: options.comments.presenter.focused,
        swallowFileNavigation: IS_MAC,
      };
      return {
        kind: "file",
        owner: section,
        document,
        presentation,
        paint: prepared
          ? { status: "ready", options: configuration, markers: prepared.source.markers }
          : { status: "unavailable", options: configuration, message: message! },
      };
    },
  };
  return section;
}
