import { ChevronLeft, ChevronRight } from "lucide-solid";
import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import type { AgentWalkthrough, ClientSession } from "../../bridge";
import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import type { ReviewCopyScope } from "../../editor/editor-host";
import { AgentCardTabFrame } from "../AgentCardTabFrame";
import type { WalkthroughQuestion as Question } from "../AgentPaneModel";
import type { AgentBlockTranscriptEntry } from "../AgentPaneTranscriptTypes";
import { ownCommand, ownsCommand } from "../agent-command-owner";
import { agentBlock } from "../block-store";
import { agentPaneModel } from "../pane-store";
import { createSelectionActions } from "../selection-actions";
import { WalkthroughQuestion } from "./WalkthroughQuestion";
import { EffectTag, Prose, type StopContext, WalkthroughStop } from "./WalkthroughStop";
import { askFrom, type WalkthroughAnchor } from "./walkthrough-ask";
import {
  emptyView,
  stopRequested,
  takeRequestedStop,
  type WalkthroughViewState,
} from "./walkthrough-view";
import "./walkthrough.css";

/** A walkthrough read in an editor tab: one centred column, its stops listed along the bottom. */
export default function WalkthroughTab(props: {
  session: ClientSession;
  path: string;
  createScope: () => ReviewCopyScope;
  viewState: () => WalkthroughViewState | null;
  setViewState: (state: WalkthroughViewState) => void;
  bind(element: HTMLElement): () => void;
}): JSX.Element {
  const scope = props.createScope();
  onCleanup(() => scope.dispose());
  return (
    <AgentCardTabFrame
      session={props.session}
      kind="walkthrough"
      path={props.path}
      noun="walkthrough"
      class="walkthrough-tab"
      bind={props.bind}
    >
      {(entry) => (
        <Show
          when={agentBlock(props.session, (entry() as AgentBlockTranscriptEntry).block.key)?.block}
        >
          {(block) => (
            <Show when={block().walkthrough}>
              {(walkthrough) => (
                <Page
                  session={props.session}
                  path={props.path}
                  scope={scope}
                  blockId={block().id}
                  conversationId={entry().conversationId ?? null}
                  title={block().title}
                  walkthrough={walkthrough()}
                  view={() => props.viewState() ?? emptyView()}
                  setView={(patch) =>
                    props.setViewState({ ...(props.viewState() ?? emptyView()), ...patch })
                  }
                />
              )}
            </Show>
          )}
        </Show>
      )}
    </AgentCardTabFrame>
  );
}

function Page(props: {
  session: ClientSession;
  path: string;
  scope: ReviewCopyScope;
  blockId: string;
  conversationId: string | null;
  title: string;
  walkthrough: AgentWalkthrough;
  view: () => WalkthroughViewState;
  setView: (patch: Partial<WalkthroughViewState>) => void;
}): JSX.Element {
  let root!: HTMLDivElement;
  const stops = () => props.walkthrough.stops;
  const sources = new Map<string, Promise<string>>();
  const source = (hash: string): Promise<string> => {
    let text = sources.get(hash);
    if (text === undefined) {
      text = props.session
        .feature("agent")
        .request<{ text: string }, unknown>("walkthroughSource", {
          conversationId: props.conversationId,
          blockId: props.blockId,
          hash,
        })
        .then((reply) => reply.text);
      sources.set(hash, text);
    }
    return text;
  };

  // Questions by the place they were asked; one whose place is gone joins its stop's end, or the page's.
  const questions = createMemo(() =>
    (agentPaneModel(props.session)?.questions() ?? []).filter(
      (question) => question.blockId === props.blockId && question.entry.asideCollapsed !== true,
    ),
  );
  const places = createMemo(() => {
    const valid = new Set(["problem", "gist", "closing"]);
    for (const stop of stops()) {
      for (const part of ["lead", "state", "why", "predict"]) valid.add(`${stop.id}/${part}`);
      stop.code.forEach((code, index) => {
        for (let line = code.from; line <= code.to; line++)
          valid.add(`${stop.id}/code/${index}/${line}`);
      });
    }
    return valid;
  });
  const placeOf = (question: Question): { place: string; moved: boolean } => {
    if (places().has(question.place)) return { place: question.place, moved: false };
    const stop = question.place.split("/")[0]!;
    return {
      place: stops().some((candidate) => candidate.id === stop) ? `${stop}/end` : "end",
      moved: true,
    };
  };
  const cardsAt = (place: string): JSX.Element => (
    <For each={questions().filter((question) => placeOf(question).place === place)}>
      {(question) => (
        <WalkthroughQuestion
          question={question}
          session={props.session}
          moved={placeOf(question).moved}
        />
      )}
    </For>
  );

  // Selecting text anywhere on the page offers the selection actions; Send asks about it where it was selected.
  let anchor: WalkthroughAnchor | null = null;
  const actions = createSelectionActions(
    document.body,
    props.session,
    (session, selection, comment) =>
      anchor === null ? null : askFrom(anchor)(session, selection, comment),
  );
  onCleanup(actions.dispose);
  const label = (place: string): string => {
    const index = stops().findIndex((stop) => place.startsWith(`${stop.id}/`));
    return `Walkthrough "${props.title}"${index < 0 ? "" : ` · stop ${index + 1}, ${stops()[index]!.title}`}`;
  };
  const offer = (place: string, text: string, point: { x: number; y: number }): void => {
    anchor = { blockId: props.blockId, place, label: label(place) };
    actions.show({ label: label(place), text }, point, () => {});
  };
  const onProseSelect = (): void => {
    const selection = document.getSelection();
    const text = selection?.toString().trim() ?? "";
    if (
      selection === null ||
      selection.isCollapsed ||
      text === "" ||
      !root.contains(selection.anchorNode)
    )
      return;
    const range = selection.getRangeAt(0);
    const element = (node: Node | null) =>
      (node instanceof Element ? node : node?.parentElement) ?? null;
    const host = element(range.endContainer)?.closest<HTMLElement>("[data-place]");
    if (host == null || element(range.startContainer)?.closest("[data-comment-box]")) return;
    const box = [...range.getClientRects()].at(-1) ?? range.getBoundingClientRect();
    offer(host.dataset.place!, text, { x: box.right, y: box.bottom });
  };

  const context: StopContext = {
    session: props.session,
    get root() {
      return props.walkthrough.root;
    },
    scope: props.scope,
    get files() {
      return props.walkthrough.files;
    },
    source,
    questionsAt: cardsAt,
    codeQuestions: (stopId, index) =>
      questions().flatMap((question) => {
        const { place, moved } = placeOf(question);
        const prefix = `${stopId}/code/${index}/`;
        return place.startsWith(prefix)
          ? [
              {
                kind: "question" as const,
                key: `question:${question.entry.conversationId}`,
                line: Number(place.slice(prefix.length)),
                render: () => (
                  <WalkthroughQuestion question={question} session={props.session} moved={moved} />
                ),
              },
            ]
          : [];
      }),
    openFolds: () => new Set(props.view().openFolds),
    toggleFold: (key) => {
      const open = new Set(props.view().openFolds);
      if (!open.delete(key)) open.add(key);
      props.setView({ openFolds: [...open] });
    },
    revealed: (stopId) => props.view().revealed.includes(stopId),
    reveal: (stopId) => props.setView({ revealed: [...props.view().revealed, stopId] }),
    onCodeSelect: offer,
  };

  // The stop being read is the last whose heading has passed the top third of the tab.
  const [current, setCurrent] = createSignal(0);
  const scroller = (): HTMLElement => root.closest<HTMLElement>(".editor-agent-view") ?? root;
  const sections = (): HTMLElement[] => [...root.querySelectorAll<HTMLElement>("[data-stop]")];
  const track = (): void => {
    const line = scroller().getBoundingClientRect().top + scroller().clientHeight / 3;
    let at = 0;
    sections().forEach((section, index) => {
      if (section.getBoundingClientRect().top <= line) at = index;
    });
    setCurrent(at);
    const id = stops()[at]?.id;
    if (id !== undefined && id !== props.view().stopId) {
      props.setView({ stopId: id, read: [...new Set([...props.view().read, id])] });
    }
  };
  const go = (index: number): void => {
    const section = sections()[index];
    if (section === undefined) return;
    const area = scroller();
    const top = area.scrollTop + section.getBoundingClientRect().top - area.getBoundingClientRect().top;
    area.scrollTo({ top: top - 8, behavior: "smooth" });
  };
  onMount(() => {
    const keyUp = (event: KeyboardEvent): void => {
      if (event.shiftKey) onProseSelect();
    };
    const listeners = [
      ["pointerup", onProseSelect],
      ["keyup", keyUp],
      ["focusin", own],
      ["pointerdown", own],
    ] as const;
    for (const [type, listener] of listeners)
      root.addEventListener(type, listener as EventListener);
    onCleanup(() => {
      for (const [type, listener] of listeners)
        root.removeEventListener(type, listener as EventListener);
    });
    const area = scroller();
    area.addEventListener("scroll", track, { passive: true });
    onCleanup(() => area.removeEventListener("scroll", track));
    const saved = props.view().stopId;
    const index = stops().findIndex((stop) => stop.id === saved);
    if (index > 0) sections()[index]?.scrollIntoView();
  });
  // A stop picked on the pane's card.
  createEffect(() => {
    if (!stopRequested(props.path)) return;
    const index = stops().findIndex((stop) => stop.id === takeRequestedStop(props.path));
    queueMicrotask(() => go(index));
  });
  // The stop commands act on the walkthrough the user last pressed or focused.
  let releases: (() => void)[] = [];
  onCleanup(() => {
    for (const release of releases) release();
  });
  const own = (event: Event): void => {
    if (ownsCommand(CommandIds.nextWalkthroughStop, releases[0] ?? null)) return;
    const element = event.currentTarget as Element;
    const step = (delta: number) => (): boolean => {
      go(current() + delta);
      return true;
    };
    releases = [
      ownCommand(CommandIds.nextWalkthroughStop, "walkthroughFocused", element, step(1), false),
      ownCommand(
        CommandIds.previousWalkthroughStop,
        "walkthroughFocused",
        element,
        step(-1),
        false,
      ),
    ];
  };

  return (
    <div class="walkthrough-page" ref={root}>
      <div class="walkthrough-column">
        <h1 class="walkthrough-title">{props.title}</h1>
        <Prose
          session={props.session}
          class=""
          place="problem"
          content={props.walkthrough.problem}
          after={cardsAt("problem")}
        />
        <div class="walkthrough-gist">
          <div class="ui-section-label">The short version</div>
          <Prose
            session={props.session}
            class=""
            place="gist"
            content={props.walkthrough.gist}
            after={cardsAt("gist")}
          />
        </div>
        <Show when={props.walkthrough.example}>
          {(example) => <p class="walkthrough-example">{example()}</p>}
        </Show>
        <Show when={props.walkthrough.change}>
          {(change) => (
            <div class="walkthrough-update">
              <div class="ui-section-label">Updated after the change</div>
              <p>{change()}</p>
              <For
                each={stops()
                  .map((stop, index) => ({ stop, index }))
                  .filter(({ stop }) => stop.effect)}
              >
                {({ stop, index }) => (
                  <button type="button" class="ui-row" onClick={() => go(index)}>
                    <span class="walkthrough-number">{index + 1}</span>
                    <span class="ui-row-name">{stop.title}</span>
                    <EffectTag effect={stop.effect ?? null} />
                  </button>
                )}
              </For>
            </div>
          )}
        </Show>
        <For each={stops()}>
          {(stop, index) => (
            <>
              <WalkthroughStop
                stop={stop}
                index={index()}
                count={stops().length}
                context={context}
              />
              {cardsAt(`${stop.id}/end`)}
            </>
          )}
        </For>
        <Show when={props.walkthrough.closing}>
          {(closing) => (
            <Prose
              session={props.session}
              class=""
              place="closing"
              content={closing()}
              after={cardsAt("closing")}
            />
          )}
        </Show>
        {cardsAt("end")}
      </div>
      <nav class="walkthrough-strip" aria-label="Stops">
        <span class="ui-stepper">
          <button
            type="button"
            title={`Previous Stop${keyHint(CommandIds.previousWalkthroughStop)}`}
            aria-label="Previous Stop"
            disabled={current() === 0}
            onClick={() => go(current() - 1)}
          >
            <ChevronLeft class="ui-icon" />
          </button>
          <button
            type="button"
            title={`Next Stop${keyHint(CommandIds.nextWalkthroughStop)}`}
            aria-label="Next Stop"
            disabled={current() >= stops().length - 1}
            onClick={() => go(current() + 1)}
          >
            <ChevronRight class="ui-icon" />
          </button>
        </span>
        <For each={stops()}>
          {(stop, index) => (
            <button
              type="button"
              class="walkthrough-strip-stop"
              classList={{
                "is-current": index() === current(),
                "is-read": props.view().read.includes(stop.id),
              }}
              title={stop.title}
              onClick={() => go(index())}
            >
              <b>{index() + 1}</b>
              <span>{stop.name}</span>
              <Show when={stop.effect}>
                {(effect) => <span class={`walkthrough-dot is-${effect()}`} />}
              </Show>
            </button>
          )}
        </For>
      </nav>
    </div>
  );
}
