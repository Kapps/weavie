// An editor embedded in another surface: the shared settings, its own overflow layer, the height of its content, and
// hidden lines with a band to click that stands for them. The review's files and a walkthrough's code build on it.

import { interfaceLine } from "../fonts";
import { hiddenAreas } from "./hidden-areas";
import {
  createEmbeddedEditor,
  createOverflowWidgets,
  embeddedEditorChrome,
  monaco,
} from "./monaco-setup";
import "./code-view.css";

// A line of interface text inside the band's borders and padding.
const bandHeight = (): number => interfaceLine() + 8;
// Lucide's chevrons-up-down.
const EXPAND_ICON =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m7 15 5 5 5-5"/><path d="m7 9 5-5 5 5"/></svg>';

/** A clickable band above line `beforeLine`, which usually stands for the hidden lines it sits on. */
export interface CodeBand {
  beforeLine: number;
  label: string;
  title: string;
  open: () => void;
}

export interface CodeView {
  editor: monaco.editor.IStandaloneCodeEditor;
  /** The layer holding the editor's overflow widgets. */
  widgets: HTMLElement;
  /** Hides `hidden` and draws `bands`, replacing the last call's. */
  setHidden(hidden: readonly monaco.IRange[], bands: readonly CodeBand[]): void;
  dispose(): void;
}

export interface CodeViewOptions {
  mount: HTMLElement;
  /** Where the overflow widgets live, outside any transformed content. */
  widgetsParent: HTMLElement;
  model: monaco.editor.ITextModel;
  dimension: monaco.editor.IDimension;
  overrides: monaco.editor.IEditorOptions;
  /** The owner of the view's hidden lines, so another owner's stay. */
  hiddenSource: string;
  onHeight: (height: number) => void;
  /** A plain click (the left button, no modifier) the bands didn't take. */
  onClick: (event: monaco.editor.IEditorMouseEvent) => void;
}

const plain = (event: monaco.editor.IEditorMouseEvent): boolean => {
  const { leftButton, shiftKey, ctrlKey, metaKey, altKey } = event.event;
  return leftButton && !shiftKey && !ctrlKey && !metaKey && !altKey;
};

export function createCodeView(options: CodeViewOptions): CodeView {
  const { mount } = options;
  const widgets = createOverflowWidgets(options.widgetsParent);
  const editor = createEmbeddedEditor(
    mount,
    options.model,
    { dimension: options.dimension, overflowWidgetsDomNode: widgets },
    { ...embeddedEditorChrome, ...options.overrides },
  );
  interface Band {
    band: CodeBand;
    zone: monaco.editor.IViewZone & { marginDomNode: HTMLElement };
  }
  let bands = new Map<string, Band>();
  let hovered: Band | undefined;
  const hover = (band: Band | undefined): void => {
    if (hovered === band) return;
    for (const node of [hovered?.zone.domNode, hovered?.zone.marginDomNode])
      node?.classList.remove("hover");
    for (const node of [band?.zone.domNode, band?.zone.marginDomNode]) node?.classList.add("hover");
    hovered = band;
    mount.classList.toggle("code-band-hover", band !== undefined);
    mount.title = band?.band.title ?? "";
  };
  const zoneOf = (band: CodeBand): Band["zone"] => {
    // Monaco forces zone nodes to display:block, so each half lays out through an inner row.
    const node = document.createElement("div");
    node.className = "code-band";
    const label = document.createElement("span");
    label.textContent = band.label;
    node.append(label);
    node.title = band.title;
    // The gutter half carries the band across the line numbers, with its expand glyph under them.
    const margin = document.createElement("div");
    margin.className = "code-band-margin";
    margin.innerHTML = `<span>${EXPAND_ICON}</span>`;
    return {
      afterLineNumber: band.beforeLine - 1,
      heightInPx: bandHeight(),
      domNode: node,
      marginDomNode: margin,
      showInHiddenAreas: true,
    };
  };
  // Monaco's text layer covers view zones, so a band's hover and click come from the editor's mouse events.
  const bandAt = (event: monaco.editor.IEditorMouseEvent): Band | undefined => {
    const { target } = event;
    return target.type === monaco.editor.MouseTargetType.CONTENT_VIEW_ZONE ||
      target.type === monaco.editor.MouseTargetType.GUTTER_VIEW_ZONE
      ? bands.get(target.detail.viewZoneId)
      : undefined;
  };
  const measure = (): void => options.onHeight(editor.getContentHeight());
  // A click is the press and release on one target; a drag between two keeps Monaco's selection.
  let pressed: monaco.editor.IEditorMouseEvent | null = null;
  const subscriptions = [
    editor.onDidContentSizeChange(measure),
    editor.onMouseLeave(() => hover(undefined)),
    editor.onMouseMove((event) => hover(bandAt(event))),
    editor.onMouseDown((event) => {
      pressed = plain(event) ? event : null;
    }),
    editor.onMouseUp((event) => {
      const down = pressed;
      pressed = null;
      if (down === null) return;
      const band = bandAt(event);
      if (band !== undefined) {
        if (bandAt(down) === band) band.band.open();
      } else if (
        bandAt(down) === undefined &&
        down.target.type === event.target.type &&
        down.target.position?.lineNumber === event.target.position?.lineNumber
      )
        options.onClick(event);
    }),
  ];
  return {
    editor,
    widgets,
    setHidden: (hidden, next) => {
      hiddenAreas(editor).setHiddenAreas([...hidden], options.hiddenSource);
      editor.changeViewZones((accessor) => {
        for (const id of bands.keys()) accessor.removeZone(id);
        hover(undefined);
        bands = new Map(
          next.map((band) => {
            const zone = zoneOf(band);
            return [accessor.addZone(zone), { band, zone }];
          }),
        );
      });
      measure();
    },
    dispose: () => {
      for (const subscription of subscriptions) subscription.dispose();
      editor.dispose();
      widgets.remove();
    },
  };
}
