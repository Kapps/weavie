import { ArrowDown, ArrowUp } from "lucide-solid";
import { createEffect, createMemo, createSignal, type JSX, on, onCleanup, Show } from "solid-js";
import { setContext } from "../commands/context";
import { liveKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { AgentComposer } from "./AgentComposer";
import { createAgentPaneScroll } from "./AgentPaneScroll";
import { AgentEmptyState } from "./AgentTranscript";
import {
  createTranscriptViewport,
  type TranscriptPosition,
  type TranscriptViewport,
} from "./AgentTranscriptViewport";
import type { AgentPaneModel } from "./pane-store";

interface ViewportSnapshot {
  expandedDetails: ReadonlySet<string>;
  followingLatest: boolean;
  generation: number;
  measurements: ReadonlyMap<string, number>;
  position: TranscriptPosition | null;
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
  const [viewport, setViewport] = createSignal<TranscriptViewport>();
  const stored = viewports.get(props.model);
  const saved = stored?.generation === props.model.generation() ? stored : undefined;
  const savedMeasurements =
    saved?.revision === props.model.revision() ? saved.measurements : new Map<string, number>();
  for (const id of saved?.expandedDetails ?? []) {
    props.model.setActivityExpanded(id, true);
  }
  const [expandedDetails, setExpandedDetails] = createSignal<ReadonlySet<string>>(
    saved?.expandedDetails ?? new Set(),
  );
  const [scrollEdgeHovered, setScrollEdgeHovered] = createSignal(false);
  const [touchNavigationActive, setTouchNavigationActive] = createSignal(false);
  createEffect(
    on(
      props.model.generation,
      () => {
        setExpandedDetails(new Set<string>());
        scroll.jumpToLatest();
      },
      { defer: true },
    ),
  );
  const turnNavigable = createMemo(
    () => !props.model.turnActive() && props.model.agentTurnStartId() !== null,
  );
  const scroll = createAgentPaneScroll(
    props.model.session,
    viewport,
    () => (body === undefined ? 0 : Number.parseFloat(getComputedStyle(body).lineHeight)),
    props.model.agentTurnStartIndex,
    turnNavigable,
    saved?.followingLatest ?? true,
  );
  createTranscriptViewport({
    body: () => body,
    model: props.model,
    expandedDetails,
    followingLatest: scroll.followingLatest,
    initialPosition: saved?.position ?? null,
    initialMeasurements: savedMeasurements,
    initialWidth: saved?.width ?? 0,
    onDispose: (view) => {
      viewports.set(props.model, {
        expandedDetails: new Set(expandedDetails()),
        followingLatest: scroll.followingLatest(),
        generation: props.model.generation(),
        measurements: view.snapshot(),
        position: view.readingPosition(),
        revision: props.model.revision(),
        width: body?.clientWidth ?? 0,
      });
    },
    onReady: setViewport,
    onWillScroll: scroll.onWillScroll,
    onDidScroll: scroll.onDidScroll,
    onDetailsToggle: (entryId, open) => {
      props.model.setActivityExpanded(entryId, open);
      setExpandedDetails((current) => toggleMember(current, entryId, open));
    },
  });
  createEffect(() => setContext("agentTurnNavigable", turnNavigable()));
  onCleanup(() => setContext("agentTurnNavigable", false));

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
        >
          <div class="agent-empty-slot">
            <Show when={props.model.entries.length === 0}>
              <AgentEmptyState compact={props.compact} providerName={props.providerName} />
            </Show>
          </div>
        </div>
        <div
          class="agent-scroll-nav"
          classList={{
            "agent-scroll-nav-edge-hovered": scrollEdgeHovered(),
            "agent-scroll-nav-touch-active": touchNavigationActive(),
          }}
          style={`right: ${
            scrollNavigationOverlayScrollbarClearance + scrollNavigationScrollbarGap
          }px`}
        >
          <Show when={turnNavigable() && scroll.agentTurnStartAbove()}>
            <AgentScrollNavigationButton
              commandId={CommandIds.agentJumpToTurn}
              edge="start"
              label="Jump to turn"
              run={scroll.jumpToTurn}
              title="Jump to the start of this agent turn"
            />
          </Show>
          <Show when={!scroll.followingLatest()}>
            <AgentScrollNavigationButton
              commandId={CommandIds.agentJumpToLatest}
              edge="latest"
              label="Jump to latest"
              run={scroll.jumpToLatest}
              title="Scroll to the latest activity and follow it"
            />
          </Show>
        </div>
      </div>
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
