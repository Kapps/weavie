import { createVirtualizer, type VirtualItem } from "@tanstack/solid-virtual";
import { ArrowDown, ArrowUp } from "lucide-solid";
import {
  createEffect,
  createMemo,
  createSignal,
  type JSX,
  on,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentBackgroundTray } from "./AgentBackgroundTray";
import { AgentComposer } from "./AgentComposer";
import { estimateEntrySize } from "./AgentPaneEstimate";
import { createAgentPaneLayout } from "./AgentPaneLayout";
import { type AgentPaneScrollState, createAgentPaneScroll } from "./AgentPaneScroll";
import { createAgentPaneWheel } from "./AgentPaneWheel";
import { AgentTranscript } from "./AgentTranscript";
import { agentBackground, backgroundRunning } from "./agent-background-store";
import { agentControlState } from "./agent-controls-store";
import type { AgentPaneModel } from "./pane-store";

interface ViewportSnapshot extends AgentPaneScrollState {
  expandedDetails: ReadonlySet<string>;
  generation: number;
  measurements: VirtualItem[];
  /** Distance from the bottom of the transcript to the bottom of the viewport. */
  offset: number;
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

export function AgentPaneBody(props: {
  active: boolean;
  compact: boolean;
  inputProtocol: number;
  model: AgentPaneModel;
  providerName: string;
}): JSX.Element {
  let body: HTMLDivElement | undefined;
  const stored = viewports.get(props.model);
  const saved = stored?.generation === props.model.generation() ? stored : undefined;
  for (const id of saved?.expandedDetails ?? []) {
    props.model.setActivityExpanded(id, true);
  }
  const [expandedDetails, setExpandedDetails] = createSignal<ReadonlySet<string>>(
    saved?.expandedDetails ?? new Set(),
  );
  const [scrollEdgeHovered, setScrollEdgeHovered] = createSignal(false);
  const [scrollbarInlineSize, setScrollbarInlineSize] = createSignal(0);
  const [touchNavigationActive, setTouchNavigationActive] = createSignal(false);
  createEffect(
    on(
      props.model.generation,
      () => {
        setExpandedDetails(new Set<string>());
      },
      { defer: true },
    ),
  );
  const wheel = createAgentPaneWheel(
    () => body,
    (delta) => scroll.onWheelIntent(delta),
    () => scroll.onWheelSettled(),
    { start: () => scroll.jumpToStart(), end: () => scroll.jumpToLatest() },
  );
  const scroll = createAgentPaneScroll(
    props.model,
    () => body,
    saved,
    () => virtualizer,
    (index) => layout.flip(index),
    () => turnNavigable(),
    wheel,
  );
  const turnNavigable = createMemo(
    () => !props.model.turnActive() && scroll.shown().agentTurnStartId !== null,
  );
  const layout = createAgentPaneLayout(
    scroll.shown,
    props.model.generation,
    scroll.anchorId,
    () => virtualizer,
  );
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
    measureElement: (element): number => element.getBoundingClientRect().height,
    observeElementOffset: scroll.observeOffset,
    overscan: 4,
    scrollToFn: scroll.scrollTo,
  });
  // The scroller's own anchoring keeps the reader still; the virtualizer never corrects scroll position.
  virtualizer.shouldAdjustScrollPositionOnItemSizeChange = () => false;
  createEffect(() => setContext("agentTurnNavigable", turnNavigable()));
  onCleanup(() => setContext("agentTurnNavigable", false));
  createEffect(() =>
    setContext("agentRewindable", agentControlState(props.model.session).rewindable),
  );
  onCleanup(() => setContext("agentRewindable", false));
  createEffect(() =>
    setContext(
      "agentBackgroundActive",
      agentBackground(props.model.session).some(backgroundRunning),
    ),
  );
  onCleanup(() => setContext("agentBackgroundActive", false));
  onMount(() => {
    const element = body;
    if (element === undefined) {
      return;
    }
    if (saved !== undefined && saved.width !== element.clientWidth) {
      virtualizer.measure();
    }
    // After the virtualizer's own listener, so the rows for the new position are already rendered.
    element.addEventListener("scroll", scroll.onScroll, { passive: true });
    onCleanup(() => element.removeEventListener("scroll", scroll.onScroll));
    const onViewportResize = (): void => {
      setScrollbarInlineSize(element.offsetWidth - element.clientWidth);
      scroll.onLayout();
    };
    onViewportResize();
    const observer = new ResizeObserver(onViewportResize);
    observer.observe(element);
    onCleanup(() => observer.disconnect());
  });
  onCleanup(() => {
    viewports.set(props.model, {
      ...scroll.snapshot(),
      expandedDetails: new Set(expandedDetails()),
      generation: props.model.generation(),
      measurements: virtualizer.takeSnapshot(),
      offset: -(body?.scrollTop ?? 0),
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
    <>
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
            onDetailsToggle={(entryId, open) =>
              scroll.applyUserChange(() => {
                props.model.setActivityExpanded(entryId, open);
                setExpandedDetails((current) => toggleMember(current, entryId, open));
              })
            }
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
          <Show when={turnNavigable() && scroll.turnStartAbove()}>
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
      <AgentBackgroundTray session={props.model.session} />
      <AgentComposer
        active={props.active}
        compact={props.compact}
        history={props.model.history()}
        inputProtocol={props.inputProtocol}
        interruptible={props.model.interruptible()}
        latestPlan={props.model.latestPlan()}
        pendingApprovalId={props.model.keyboardApprovalId()}
        pendingKind={props.model.pendingRequestKind()}
        pendingLegacyImageCount={props.model.pendingLegacyImageCount()}
        session={props.model.session}
        turnActive={props.model.turnActive()}
        turnStartedAt={props.model.turnStartedAt()}
        onSubmitted={scroll.jumpToLatest}
      />
    </>
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
