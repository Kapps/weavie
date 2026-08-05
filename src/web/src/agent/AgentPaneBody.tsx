import { createVirtualizer, elementScroll, type VirtualItem } from "@tanstack/solid-virtual";
import {
  createEffect,
  createMemo,
  createSignal,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentComposer } from "./AgentComposer";
import { createAgentPaneScroll } from "./AgentPaneScroll";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { AgentTranscript } from "./AgentTranscript";
import { TranscriptEntry } from "./AgentTranscriptEntry";
import type { AgentPaneModel } from "./pane-store";

interface ViewportSnapshot {
  expandedDetails: ReadonlySet<string>;
  followingLatest: boolean;
  generation: number;
  measurements: VirtualItem[];
  offset: number;
  revision: number;
  width: number;
}

const viewports = new WeakMap<AgentPaneModel, ViewportSnapshot>();
const scrollNavigationRevealWidth = 40;
const scrollNavigationOverlayScrollbarClearance = 12;
const scrollNavigationScrollbarGap = 4;

function AgentScrollNavigationButton(props: {
  commandId: string;
  edge: "start" | "latest";
  glyph: string;
  label: string;
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
      title={title()}
      onClick={() => props.run()}
    >
      <span aria-hidden="true">{props.glyph}</span>
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
  const detach = props.model.attach();
  onCleanup(detach);
  let body: HTMLDivElement | undefined;
  let virtualizerChanged = (_sync: boolean): void => {};
  let virtualizerScroll = (_top: number): void => {};
  const stored = viewports.get(props.model);
  const saved = stored?.generation === props.model.generation() ? stored : undefined;
  const savedMeasurements = saved?.revision === props.model.revision() ? saved.measurements : [];
  const [expandedDetails, setExpandedDetails] = createSignal<ReadonlySet<string>>(
    saved?.expandedDetails ?? new Set(),
  );
  const [scrollEdgeHovered, setScrollEdgeHovered] = createSignal(false);
  const [scrollbarInlineSize, setScrollbarInlineSize] = createSignal(0);
  const [touchNavigationActive, setTouchNavigationActive] = createSignal(false);
  const turnNavigable = createMemo(
    () => !props.model.turnActive() && props.model.agentTurnStartId() !== null,
  );
  const virtualizer = createVirtualizer<HTMLDivElement, HTMLDivElement>({
    get count() {
      return props.model.entries.length;
    },
    getItemKey: (index) =>
      `${props.model.generation()}\0${props.model.entries[index]?.id ?? index}`,
    getScrollElement: () => body ?? null,
    estimateSize: (index) => estimateEntrySize(props.model.entries[index]),
    anchorTo: "end",
    initialMeasurementsCache: savedMeasurements,
    initialOffset: saved?.offset ?? 0,
    measureElement: (element) => element.getBoundingClientRect().height,
    onChange: (_instance, sync) => virtualizerChanged(sync),
    overscan: 4,
    scrollToFn: (offset, options, instance) => {
      virtualizerScroll(offset + (options.adjustments ?? 0));
      elementScroll(offset, options, instance);
    },
    useAnimationFrameWithResizeObserver: true,
  });
  const scroll = createAgentPaneScroll(
    props.model.session,
    () => body,
    virtualizer,
    props.model.agentTurnStartIndex,
    turnNavigable,
    props.model.revision,
    saved?.followingLatest ?? true,
  );
  virtualizerChanged = scroll.onVirtualizerChange;
  virtualizerScroll = scroll.noteControllerScroll;

  createEffect(() => setContext("agentTurnNavigable", turnNavigable()));
  onCleanup(() => setContext("agentTurnNavigable", false));
  onMount(() => {
    const element = body;
    if (element === undefined) {
      return;
    }
    if (saved !== undefined && saved.width !== element.clientWidth) {
      virtualizer.measure();
    }
    const measureScrollbar = (): void => {
      setScrollbarInlineSize(element.offsetWidth - element.clientWidth);
    };
    measureScrollbar();
    const observer = new ResizeObserver(measureScrollbar);
    observer.observe(element);
    onCleanup(() => observer.disconnect());
  });
  onCleanup(() => {
    viewports.set(props.model, {
      expandedDetails: new Set(expandedDetails()),
      followingLatest: scroll.followingLatest(),
      generation: props.model.generation(),
      measurements: virtualizer.takeSnapshot(),
      offset: body?.scrollTop ?? 0,
      revision: props.model.revision(),
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
          class="agent-body"
          ref={body}
          onPointerDown={revealTouchNavigation}
          onPointerLeave={() => setScrollEdgeHovered(false)}
          onPointerMove={updateScrollEdgeHover}
          onScroll={scroll.onScroll}
        >
          <AgentTranscript
            agentTurnStartId={props.model.agentTurnStartId()}
            compact={props.compact}
            entries={props.model.entries}
            expandedDetails={expandedDetails()}
            keyboardApprovalId={props.model.keyboardApprovalId()}
            onDetailsToggle={(entryId, open) =>
              setExpandedDetails((current) => toggleMember(current, entryId, open))
            }
            providerName={props.providerName}
            sectionLabels={props.model.sectionLabels()}
            session={props.model.session}
            showEmptyState={props.model.pinnedRequest() === null}
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
          <Show when={turnNavigable() && scroll.agentTurnStartAbove()}>
            <AgentScrollNavigationButton
              commandId={CommandIds.agentJumpToTurn}
              edge="start"
              glyph="↑"
              label="Jump to turn"
              run={scroll.jumpToTurn}
              title="Jump to the start of this agent turn"
            />
          </Show>
          <Show when={!scroll.followingLatest()}>
            <AgentScrollNavigationButton
              commandId={CommandIds.agentJumpToLatest}
              edge="latest"
              glyph="↓"
              label="Jump to latest"
              run={scroll.jumpToLatest}
              title="Scroll to the latest activity and follow it"
            />
          </Show>
        </div>
      </div>
      <Show when={props.model.pinnedRequest()?.id} keyed>
        {(_requestId) => (
          <section
            class="agent-pending-request"
            data-agent-pending-request
            aria-label="Waiting for your response"
          >
            <TranscriptEntry
              detailsExpanded={false}
              entry={props.model.pinnedRequest()!}
              keyboardApprovalId={props.model.keyboardApprovalId()}
              onDetailsToggle={() => {}}
              sectionLabel={null}
              session={props.model.session}
            />
          </section>
        )}
      </Show>
      <AgentComposer
        active={props.active}
        compact={props.compact}
        inputProtocol={props.inputProtocol}
        messages={props.model.messages()}
        session={props.model.session}
        onSubmitted={scroll.followIfNearBottom}
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

function estimateEntrySize(entry: AgentTranscriptEntry | undefined): number {
  if (entry === undefined) {
    return 48;
  }
  const contentLength = (entry.text?.length ?? 0) + (entry.summary?.length ?? 0);
  const lineHeight = entry.tone === "assistant" ? 24 : 20;
  const content = Math.max(1, Math.ceil(contentLength / 48)) * lineHeight;
  const chrome = entry.kind === "message" && entry.tone === "assistant" ? 12 : 34;
  return chrome + content;
}
