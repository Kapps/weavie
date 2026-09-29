import { trackFocus } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/dom";
import { DisposableStore } from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import { createSignal } from "solid-js";
import type { monaco } from "../monaco-setup";
import { observeReviewMouseGesture } from "./review-mouse-handoff";

/** Interaction ownership is event-driven; scrolling never inspects document focus or polls Monaco. */
export function createReviewEditorActivity(
  editor: monaco.editor.IStandaloneCodeEditor,
  widgets: HTMLElement,
) {
  const resources = new DisposableStore();
  const [focus, setFocus] = createSignal(editor.hasWidgetFocus());
  const [widgetFocus, setWidgetFocus] = createSignal(false);
  const [composition, setComposition] = createSignal(false);
  const [gesture, setGesture] = createSignal(false);
  try {
    resources.add(editor.onDidFocusEditorWidget(() => setFocus(true)));
    resources.add(editor.onDidBlurEditorWidget(() => setFocus(false)));
    resources.add(editor.onDidCompositionStart(() => setComposition(true)));
    resources.add(editor.onDidCompositionEnd(() => setComposition(false)));
    const widgetTracker = resources.add(trackFocus(widgets));
    resources.add(widgetTracker.onDidFocus(() => setWidgetFocus(true)));
    resources.add(widgetTracker.onDidBlur(() => setWidgetFocus(false)));
    resources.add(observeReviewMouseGesture(editor, setGesture));
    return {
      retained: () => focus() || widgetFocus() || composition() || gesture(),
      dispose: () => resources.dispose(),
    };
  } catch (error) {
    resources.dispose();
    throw error;
  }
}
