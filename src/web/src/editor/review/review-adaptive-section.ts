import { IS_MAC } from "../../commands/keybindings";
import type { ReviewScopeState } from "../inline-diff";
import type { monaco } from "../monaco-setup";
import type { TextLocation } from "../nav-history";
import type { ReviewCommentLayout } from "./review-comment-layout";
import type { ReviewEditor } from "./review-editor";
import type { ReviewActionPresentation } from "./review-file-actions";
import type { PassiveReviewPresentation } from "./review-passive-presentation";
import type { ReviewSection } from "./review-section";
import type { ReviewToolbarTarget } from "./review-toolbar-state";

/** Commands and reading anchors belong to the file, not to its current paint adapter. */
export function createAdaptiveReviewSection(options: {
  path: string;
  scope: ReviewScopeState;
  valid(): boolean;
  passive(): PassiveReviewPresentation | undefined;
  editor(): ReviewEditor | undefined;
  activate(): ReviewEditor | undefined;
  cursor(): number;
  select(line: number): void;
  viewState(): monaco.editor.ICodeEditorViewState | null;
  save(state: monaco.editor.ICodeEditorViewState): void;
  comments: ReviewCommentLayout;
}): ReviewSection & { passiveTarget(): ReviewToolbarTarget } {
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
  const reveal = (line: number): void => {
    options.select(line);
    const passive = options.passive();
    const painted = passive?.displayed();
    if (painted)
      passive!.reveal(
        painted.rendered.geometry.topForLineNumber(line) -
          (passive!.bounds().height - painted.rendered.lineHeight) / 2,
      );
  };
  const restore = (location: TextLocation): void => {
    const editor = options.editor();
    if (editor) {
      editor.restore(location);
      return;
    }
    if (location.viewState) options.save(location.viewState);
    options.select(location.viewState?.cursorState[0]?.position.lineNumber ?? location.line);
    const passive = options.passive();
    const painted = passive?.displayed();
    if (location.anchor && painted)
      passive!.reveal(
        painted.rendered.geometry.topForLineNumber(location.anchor.line) + location.anchor.offset,
      );
    else reveal(location.line);
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
    restore,
    revealFileStart: (line) => {
      const editor = options.editor();
      if (editor) editor.revealFileStart(line);
      else {
        options.select(line);
        options.passive()?.reveal(0);
      }
    },
    focus: () => options.activate()?.focus(),
    target: () => options.editor()?.target() ?? section.passiveTarget(),
    passiveTarget: () => {
      const passive = options.passive();
      const prepared = passive?.current();
      const document = prepared?.document ?? passive?.document();
      const message = passive?.error();
      const configuration = document?.actions.options;
      if (!document || !configuration || (!prepared && !message)) return { kind: "none" };
      const presentation: ReviewActionPresentation = {
        scope: options.scope,
        valid: () =>
          options.valid() &&
          !options.editor() &&
          options.passive()?.current() === prepared &&
          document.actions.options === configuration &&
          (prepared !== undefined ||
            (options.passive()?.document() === document && options.passive()?.error() === message)),
        availability: () => (prepared ? "ready" : "unavailable"),
        reviewLine,
        commentLine: reviewLine,
        revealLine: reveal,
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
