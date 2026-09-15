import type {
  IListRenderer,
  IListVirtualDelegate,
} from "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/list/list";
import { ListView } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/list/listView";
import type { ScrollEvent } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import { createEffect, createSignal, getOwner, onCleanup, onMount, type Setter } from "solid-js";
import { render } from "solid-js/web";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { estimateEntrySize } from "./AgentPaneEstimate";
import type { AgentPaneModel } from "./AgentPaneModel";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentTranscriptRow } from "./AgentTranscript";
import { installTranscriptInput } from "./AgentTranscriptInput";
import { ownsTouchGesture, wheelMotion } from "./AgentTranscriptScrollInput";
import "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/list/list.css";
import "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/scrollbar/media/scrollbars.css";

interface Row {
  key: string;
  entry: AgentTranscriptEntry;
}
interface MountedRow {
  current: () => Row;
  setCurrent: Setter<Row>;
  setIndex: Setter<number>;
  dispose: () => void;
}
interface Template {
  element: HTMLElement;
  mounted: MountedRow | null;
}
export interface TranscriptPosition {
  key: string;
  offset: number;
}
export interface TranscriptViewport {
  height: () => number;
  contentHeight: () => number;
  offset: () => number;
  itemTop: (index: number) => number;
  jumpTo: (offset: number) => void;
  snapshot: () => ReadonlyMap<string, number>;
  readingPosition: () => TranscriptPosition | null;
}

export function createTranscriptViewport(props: {
  body: () => HTMLDivElement | undefined;
  model: AgentPaneModel;
  expandedDetails: () => ReadonlySet<string>;
  onDetailsToggle: (entryId: string, open: boolean) => void;
  followingLatest: () => boolean;
  initialPosition: TranscriptPosition | null;
  initialMeasurements: ReadonlyMap<string, number>;
  initialWidth: number;
  onDispose: (viewport: TranscriptViewport) => void;
  onReady: (viewport: TranscriptViewport) => void;
  onWillScroll: (event: ScrollEvent) => void;
  onDidScroll: (event: ScrollEvent) => void;
}): void {
  const owner = getOwner();
  let initialized = false;
  let disposed = false;
  let queued = false;
  let pending: { view: ListView<Row>; rows: Row[]; retained: string[] } | undefined;
  const [list, setList] = createSignal<ListView<Row>>();
  onMount(() => {
    const body = props.body();
    if (body === undefined) return;
    const templates = new WeakMap<Element, Template>();
    let view: ListView<Row>;
    const observer = new ResizeObserver((entries) => {
      if (body.getClientRects().length === 0) return;
      for (const entry of entries) {
        const mounted = templates.get(entry.target)?.mounted;
        if (mounted === null || mounted === undefined) continue;
        const index = view.indexOf(mounted.current());
        if (index >= 0)
          view.updateElementHeight(
            index,
            entry.target.getBoundingClientRect().height,
            view.firstVisibleIndex,
          );
      }
    });
    const renderer: IListRenderer<Row, Template> = {
      templateId: "agent-entry",
      renderTemplate(element) {
        const template = { element, mounted: null };
        templates.set(element, template);
        return template;
      },
      renderElement(row, index, template) {
        if (template.mounted !== null) {
          template.mounted.setCurrent(row);
          template.mounted.setIndex(index);
          return;
        }
        const [current, setCurrent] = createSignal(row);
        const [position, setIndex] = createSignal(index);
        const dispose = render(
          () => (
            <AgentTranscriptRow
              entry={current().entry}
              index={position()}
              previous={props.model.entries[position() - 1]}
              agentTurnStartId={props.model.agentTurnStartId()}
              expandedDetails={props.expandedDetails()}
              keyboardRequestKey={props.model.keyboardRequestKey()}
              onDetailsToggle={props.onDetailsToggle}
              sectionLabels={props.model.sectionLabels()}
              session={props.model.session}
            />
          ),
          template.element,
          undefined,
          { owner },
        );
        template.mounted = { current, setCurrent, setIndex, dispose };
        observer.observe(template.element);
      },
      disposeElement(_row, _index, template) {
        observer.unobserve(template.element);
        template.mounted?.dispose();
        template.mounted = null;
      },
      disposeTemplate(template) {
        templates.delete(template.element);
      },
    };
    const delegate: IListVirtualDelegate<Row> = {
      getTemplateId: () => renderer.templateId,
      getHeight: (row) =>
        (body.clientWidth === props.initialWidth
          ? props.initialMeasurements.get(row.key)
          : undefined) ?? estimateEntrySize(row.entry),
      hasDynamicHeight: () => true,
    };
    view = new ListView(body, delegate, [renderer], {
      supportDynamicHeights: true,
      setRowHeight: false,
      setRowLineHeight: false,
      horizontalScrolling: false,
      userSelection: true,
      scrollToActiveElement: true,
      handleMouseWheel: false,
      touchGesturePolicy: (target, deltaX, deltaY) =>
        ownsTouchGesture(target, deltaX, deltaY, body),
      smoothScrolling: currentEditorOptions().smoothScrolling,
      endAffinity: props.followingLatest,
    });
    view.domNode.setAttribute("role", "list");
    view.domNode.setAttribute("aria-label", "Agent conversation");
    view.containerDomNode.classList.add("agent-transcript");
    view.containerDomNode.dataset.agentTranscript = "";
    const willScroll = view.onWillScroll((event) => {
      if (initialized) props.onWillScroll(event);
    });
    const didScroll = view.onDidScroll((event) => {
      if (initialized) props.onDidScroll(event);
    });
    const wheel = (event: WheelEvent): void => {
      const movement = wheelMotion(event, body, currentEditorOptions().smoothScrolling);
      if (movement === null) return;
      event.preventDefault();
      view.scrollBy(movement.delta, movement.animate);
    };
    body.addEventListener("wheel", wheel, { passive: false });
    let smoothScrolling = currentEditorOptions().smoothScrolling;
    const unsubscribe = onEditorOptionsChanged(() => {
      const next = currentEditorOptions().smoothScrolling;
      if (next === smoothScrolling) return;
      smoothScrolling = next;
      view.setScrollTop(view.getScrollTop());
      view.updateOptions({ smoothScrolling });
    });
    const resize = new ResizeObserver(() => {
      if (body.getClientRects().length === 0) return;
      view.layout(body.clientHeight, body.clientWidth);
      flushPending();
    });
    resize.observe(body);
    const viewport: TranscriptViewport = {
      height: () => view.renderHeight,
      contentHeight: () => view.scrollHeight,
      offset: () => view.getScrollTop(),
      readingPosition: () => {
        const index = view.indexAt(view.getScrollTop());
        return index < view.length
          ? { key: view.element(index).key, offset: view.getScrollTop() - view.elementTop(index) }
          : null;
      },
      itemTop: (index) => view.elementTop(index),
      jumpTo: (offset) => view.setScrollTop(offset),
      snapshot: () =>
        new Map(
          Array.from({ length: view.length }, (_, index) => [
            view.element(index).key,
            view.elementHeight(index),
          ]),
        ),
    };
    const disposeInput = installTranscriptInput(
      body,
      {
        ...viewport,
        scrollBy: (delta, animate) => view.scrollBy(delta, animate),
        jumpTo: (offset) => view.setScrollTopFromInput(offset),
      },
      view.domNode,
    );
    props.onReady(viewport);
    setList(view);
    onCleanup(() => {
      disposed = true;
      pending = undefined;
      props.onDispose(viewport);
      disposeInput();
      resize.disconnect();
      observer.disconnect();
      body.removeEventListener("wheel", wheel);
      unsubscribe();
      willScroll.dispose();
      didScroll.dispose();
      view.dispose();
    });
  });
  function flushPending(): void {
    const update = pending;
    if (disposed || update === undefined || update.view.domNode.getClientRects().length === 0)
      return;
    pending = undefined;
    const { view, rows, retained } = update;
    view.setElements(rows, retained, (row) => row.key);
    if (!initialized) {
      const position = props.initialPosition;
      const index = rows.findIndex((row) => row.key === position?.key);
      const offset =
        index < 0 || position === null
          ? 0
          : view.elementTop(index) + Math.min(position.offset, view.elementHeight(index));
      view.setScrollTop(props.followingLatest() ? view.scrollHeight : offset);
      initialized = true;
    }
  }
  createEffect(() => {
    const view = list();
    const generation = props.model.generation();
    props.model.revision();
    if (view === undefined) return;
    const rows = props.model.entries.map((entry) => ({ key: `${generation}\0${entry.id}`, entry }));
    const retained = props.model.pendingRowIndexes().map((index) => rows[index]!.key);
    pending = { view, rows, retained };
    if (queued) return;
    queued = true;
    // ListView measures renderer output synchronously, after Solid's current DOM commit.
    queueMicrotask(() => {
      queued = false;
      flushPending();
    });
  });
}
