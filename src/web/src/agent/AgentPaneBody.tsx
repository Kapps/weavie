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

export function AgentPaneBody(props: {
  active: boolean;
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
    if (saved !== undefined && body !== undefined && saved.width !== body.clientWidth) {
      virtualizer.measure();
    }
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

  const commandTitle = (label: string, commandId: string): string => {
    const key = liveKeyLabel(commandId);
    return key === "" ? label : `${label} (${key})`;
  };

  return (
    <>
      <div class="agent-body-wrap">
        <div class="agent-body" ref={body} onScroll={scroll.onScroll}>
          <AgentTranscript
            agentTurnStartId={props.model.agentTurnStartId()}
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
        <Show when={turnNavigable() && scroll.followingLatest() && scroll.agentTurnStartAbove()}>
          <button
            type="button"
            class="agent-follow-pill"
            title={commandTitle("Jump to the start of this agent turn", CommandIds.agentJumpToTurn)}
            onClick={() => scroll.jumpToTurn()}
          >
            ↑ Jump to turn
          </button>
        </Show>
        <Show when={!scroll.followingLatest()}>
          <button
            type="button"
            class="agent-follow-pill"
            title={commandTitle(
              "Scroll to the latest activity and follow it",
              CommandIds.agentJumpToLatest,
            )}
            onClick={() => scroll.jumpToLatest()}
          >
            ↓ Jump to latest
          </button>
        </Show>
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
