import { createVirtualizer, type VirtualItem } from "@tanstack/solid-virtual";
import { ArrowDown, ArrowUp } from "lucide-solid";
import { createEffect, createSignal, type JSX, onCleanup, onMount, Show } from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { estimateEntrySize } from "./AgentPaneEstimate";
import { createAgentPaneLayout } from "./AgentPaneLayout";
import { type AgentPaneScrollState, createAgentPaneScroll } from "./AgentPaneScroll";
import { AgentTranscript } from "./AgentTranscript";
import type { AgentPaneModel } from "./pane-store";

interface ViewportSnapshot extends AgentPaneScrollState {
  expandedDetails: ReadonlySet<string>;
  generation: number;
  measurements: VirtualItem[];
  width: number;
}

const viewports = new WeakMap<AgentPaneModel, ViewportSnapshot>();
const scrollNavigationRevealWidth = 40;
const scrollNavigationOverlayScrollbarClearance = 12;
const scrollNavigationScrollbarGap = 4;

function AgentScrollNavigationButton(props: {
  commandId: string;
  edge: "start" | "latest";
  label: string;
  newOutput: boolean;
  run: () => boolean;
  title: string;
}): JSX.Element {
  const title = (): string => {
    const key = liveKeyLabel(props.commandId);
    return key === "" ? props.title : `${props.title} (${key})`;
  };

  return (
    <button
      type="button"
      aria-label={props.label}
      class={`agent-scroll-nav-button agent-scroll-nav-${props.edge}`}
      classList={{ "agent-scroll-nav-new-output": props.newOutput }}
      title={title()}
      onClick={() => props.run()}
      // A tap that stops momentum produces no click on iOS, so a finger lifted over the button acts.
      onTouchEnd={(event) => {
        const touch = event.changedTouches[0];
        if (touch === undefined) return;
        event.preventDefault();
        const bounds = event.currentTarget.getBoundingClientRect();
        if (
          touch.clientX >= bounds.left &&
          touch.clientX <= bounds.right &&
          touch.clientY >= bounds.top &&
          touch.clientY <= bounds.bottom
        ) {
          props.run();
        }
      }}
    >
      <Show when={props.newOutput}>New output</Show>
      <Show
        when={props.edge === "start"}
        fallback={<ArrowDown class="agent-scroll-nav-icon" aria-hidden="true" />}
      >
        <ArrowUp class="agent-scroll-nav-icon" aria-hidden="true" />
      </Show>
    </button>
  );
}

export function AgentTranscriptViewport(props: {
  compact: boolean;
  model: AgentPaneModel;
  onJumpToLatest: (jump: () => boolean) => void;
  providerName: string;
}): JSX.Element {
  let body: HTMLDivElement | undefined;
  const generation = props.model.generation();
  const stored = viewports.get(props.model);
  const saved = stored?.generation === generation ? stored : undefined;
  for (const id of saved?.expandedDetails ?? []) {
    props.model.setActivityExpanded(id, true);
  }
  const [expandedDetails, setExpandedDetails] = createSignal<ReadonlySet<string>>(
    saved?.expandedDetails ?? new Set(),
  );
  const [scrollEdgeHovered, setScrollEdgeHovered] = createSignal(false);
  const [scrollbarInlineSize, setScrollbarInlineSize] = createSignal(0);
  const [touchNavigationActive, setTouchNavigationActive] = createSignal(false);
  const scroll = createAgentPaneScroll(
    props.model,
    () => body,
    saved,
    () => virtualizer,
    { flip: (index) => layout.flip(index), virtualIndexOf: (id) => layout.virtualIndexOf(id) },
  );
  props.onJumpToLatest(scroll.jumpToLatest);
  const layout = createAgentPaneLayout(scroll.shown, () => virtualizer);
  const virtualizer = createVirtualizer<HTMLDivElement, HTMLDivElement>({
    get count() {
      return layout.count();
    },
    get getItemKey() {
      return layout.itemKey();
    },
    get rangeExtractor() {
      return layout.rangeExtractor();
    },
    getScrollElement: () => body ?? null,
    estimateSize: (index) => estimateEntrySize(layout.entryAt(index)),
    initialMeasurementsCache: saved?.shown === scroll.shown() ? saved.measurements : [],
    initialOffset: saved?.offset ?? 0,
    // A row is measured by its resize observer, after layout; registering it keeps the size it has.
    measureElement: (element, entry, instance) =>
      entry?.borderBoxSize[0]?.blockSize ??
      instance.measurementsCache[instance.indexFromElement(element)]!.size,
    observeElementOffset: scroll.observeOffset,
    overscan: 4,
    scrollToFn: scroll.scrollTo,
  });
  // The pane keeps the reader still from these measurements; the virtualizer never corrects the position.
  virtualizer.shouldAdjustScrollPositionOnItemSizeChange = () => false;
  createEffect(() => setContext("agentTurnNavigable", scroll.turnNavigable()));
  onCleanup(() => setContext("agentTurnNavigable", false));
  onMount(() => {
    const element = body!;
    if (saved !== undefined && saved.width !== element.clientWidth) {
      virtualizer.measure();
    }
    const observer = new ResizeObserver(([entry]) => {
      setScrollbarInlineSize(element.offsetWidth - element.clientWidth);
      scroll.onResize(entry!.contentRect.height);
    });
    observer.observe(element);
    onCleanup(() => observer.disconnect());
  });
  onCleanup(() => {
    viewports.set(props.model, {
      ...scroll.snapshot(),
      expandedDetails: new Set(expandedDetails()),
      generation,
      measurements: virtualizer.takeSnapshot(),
      width: body?.clientWidth ?? 0,
    });
  });

  const updateScrollEdgeHover = (event: PointerEvent & { currentTarget: HTMLDivElement }): void => {
    const bounds = event.currentTarget.getBoundingClientRect();
    setScrollEdgeHovered(event.clientX >= bounds.right - scrollNavigationRevealWidth);
  };

  const revealTouchNavigation = (event: PointerEvent): void => {
    if (event.pointerType === "touch") {
      setTouchNavigationActive(true);
    }
  };

  return (
    <div class="agent-body-wrap">
      <div
        class="agent-body agent-transcript-scroller"
        ref={body}
        tabindex="-1"
        onPointerDown={revealTouchNavigation}
        onPointerLeave={() => setScrollEdgeHovered(false)}
        onPointerMove={updateScrollEdgeHover}
      >
        <AgentTranscript
          compact={props.compact}
          entryAt={layout.entryAt}
          entryForKey={layout.entryForKey}
          expandedDetails={expandedDetails()}
          keyboardRequestKey={props.model.keyboardRequestKey()}
          onLayout={scroll.onLayout}
          onDetailsToggle={(entryId, open) => {
            props.model.setActivityExpanded(entryId, open);
            setExpandedDetails((current) => toggleMember(current, entryId, open));
          }}
          providerName={props.providerName}
          session={props.model.session}
          transcript={scroll.shown()}
          virtualizer={virtualizer}
        />
      </div>
      <div
        class="agent-scroll-nav"
        classList={{
          "agent-scroll-nav-edge-hovered": scrollEdgeHovered(),
          "agent-scroll-nav-touch-active": touchNavigationActive(),
        }}
        style={`right: ${
          Math.max(scrollbarInlineSize(), scrollNavigationOverlayScrollbarClearance) +
          scrollNavigationScrollbarGap
        }px`}
      >
        <Show when={scroll.turnNavigable() && scroll.turnStartAbove()}>
          <AgentScrollNavigationButton
            commandId={CommandIds.agentJumpToTurn}
            edge="start"
            label="Jump to turn"
            newOutput={false}
            run={scroll.jumpToTurn}
            title="Jump to the start of this agent turn"
          />
        </Show>
        <Show when={!scroll.following()}>
          <AgentScrollNavigationButton
            commandId={CommandIds.agentJumpToLatest}
            edge="latest"
            label="Jump to latest"
            newOutput={scroll.unseen()}
            run={scroll.jumpToLatest}
            title="Scroll to the latest activity and follow it"
          />
        </Show>
      </div>
    </div>
  );
}

function toggleMember(current: ReadonlySet<string>, entryId: string, included: boolean) {
  if (current.has(entryId) === included) {
    return current;
  }
  const next = new Set(current);
  if (included) {
    next.add(entryId);
  } else {
    next.delete(entryId);
  }
  return next;
}
