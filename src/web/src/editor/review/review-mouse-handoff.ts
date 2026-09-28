import { NavigationCommandRevealType } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/coreCommands";
import type { IMouseTargetOutsideEditor } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorBrowser";
import { EditorMouseEvent } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorDom";
import type { monaco } from "../monaco-setup";

interface MouseInputEditor {
  _modelData: {
    view: {
      domNode: { domNode: HTMLElement };
      _pointerHandler: {
        handler: {
          _onMouseDown(event: EditorMouseEvent, pointerId: number): void;
          _mouseDownOperation: {
            isActive(): boolean;
            _stop(): void;
            _mouseState: { startedOnLineNumbers: boolean };
            _leftRightDragScrolling: {
              start(position: IMouseTargetOutsideEditor, event: EditorMouseEvent): void;
            };
            _dispatchMouse(
              position: IMouseTargetOutsideEditor,
              inSelectionMode: boolean,
              revealType: NavigationCommandRevealType,
            ): void;
          };
        };
      };
    };
  };
}

const observedOperations = new WeakSet<object>();

/** Owns the pinned Monaco gesture hooks for one review widget, never its prototype. */
export function observeReviewMouseGesture(
  editor: monaco.editor.IStandaloneCodeEditor,
  changed: (active: boolean) => void,
): monaco.IDisposable {
  const operation = (editor as unknown as MouseInputEditor)._modelData.view._pointerHandler.handler
    ._mouseDownOperation;
  if (observedOperations.has(operation))
    throw new Error("The review mouse operation already has an owner");
  const stop = operation._stop;
  const drag = operation._leftRightDragScrolling;
  const start = drag.start;
  const stopDescriptor = Object.getOwnPropertyDescriptor(operation, "_stop");
  const startDescriptor = Object.getOwnPropertyDescriptor(drag, "start");
  let disposed = false;
  let stopPatched = false;
  let startPatched = false;
  let down: monaco.IDisposable | undefined;
  const observedStop = function (this: typeof operation): void {
    try {
      stop.call(this);
    } finally {
      if (!disposed) changed(this.isActive());
    }
  };
  const observedStart = function (
    this: typeof drag,
    position: IMouseTargetOutsideEditor,
    event: EditorMouseEvent,
  ): void {
    start.call(this, position, event);
    // Fast gutter move/up cancels Monaco's scheduled frame before it updates the selection.
    if (!disposed && operation._mouseState.startedOnLineNumbers)
      operation._dispatchMouse(position, true, NavigationCommandRevealType.None);
  };
  const dispose = (): void => {
    if (disposed) return;
    disposed = true;
    let replaced = false;
    try {
      down?.dispose();
    } finally {
      observedOperations.delete(operation);
      replaced =
        (stopPatched && operation._stop !== observedStop) ||
        (startPatched && drag.start !== observedStart);
      if (stopPatched && operation._stop === observedStop) {
        if (stopDescriptor) Object.defineProperty(operation, "_stop", stopDescriptor);
        else Reflect.deleteProperty(operation, "_stop");
      }
      if (startPatched && drag.start === observedStart) {
        if (startDescriptor) Object.defineProperty(drag, "start", startDescriptor);
        else Reflect.deleteProperty(drag, "start");
      }
    }
    if (replaced) throw new Error("Review mouse lifecycle observer was replaced");
  };
  observedOperations.add(operation);
  try {
    operation._stop = observedStop;
    stopPatched = true;
    drag.start = observedStart;
    startPatched = true;
    down = editor.onMouseDown(() => changed(operation.isActive()));
    changed(operation.isActive());
    return { dispose };
  } catch (error) {
    dispose();
    throw error;
  }
}

/** Delivers the original event to the newly activated editor without synthetic redispatch. */
export function beginExternalMouseDown(
  editor: monaco.editor.IStandaloneCodeEditor,
  original: MouseEvent,
  pointerId: number,
  renderedTarget: HTMLElement,
): void {
  const view = (editor as unknown as MouseInputEditor)._modelData.view;
  if (!view.domNode.domNode.contains(renderedTarget))
    throw new Error("The handoff target does not belong to this editor");
  const event = new EditorMouseEvent(original, false, view.domNode.domNode);
  Object.defineProperty(event, "target", { value: renderedTarget });
  view._pointerHandler.handler._onMouseDown(event, pointerId);
}
