import { createEffect, createMemo, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type { AgentWalkthrough, ClientSession, WalkthroughCode } from "../../bridge";
import type { FileModelScope } from "../../editor/editor-host";
import { AgentCardTabFrame } from "../AgentCardTabFrame";
import type { AgentBlockTranscriptEntry } from "../AgentPaneTranscriptTypes";
import { agentBlock } from "../block-store";
import { agentPaneModel } from "../pane-store";
import { WalkthroughQuestion } from "./WalkthroughQuestion";
import { EffectTag, Prose, type StopContext, WalkthroughStop } from "./WalkthroughStop";
import { WalkthroughStrip } from "./WalkthroughStrip";
import type { QuestionCard } from "./walkthrough-code";
import {
  createBlockModels,
  createSources,
  highlightQuotes,
  ownStopCommands,
  trackCurrentStop,
} from "./walkthrough-page";
import { codeLine, placeQuestions } from "./walkthrough-questions";
import { createWalkthroughSelection } from "./walkthrough-selection";
import { emptyView, requestedTarget, type WalkthroughViewState } from "./walkthrough-view";
import "./walkthrough.css";

/** A walkthrough read in an editor tab: one centred column, its stops listed along the bottom. */
export default function WalkthroughTab(props: {
  session: ClientSession;
  path: string;
  createScope: () => FileModelScope;
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
  scope: FileModelScope;
  blockId: string;
  conversationId: string | null;
  title: string;
  walkthrough: AgentWalkthrough;
  view: () => WalkthroughViewState;
  setView: (patch: Partial<WalkthroughViewState>) => void;
}): JSX.Element {
  let root!: HTMLDivElement;
  const stops = () => props.walkthrough.stops;

  const placed = createMemo(() =>
    placeQuestions(
      (agentPaneModel(props.session)?.questions() ?? []).filter(
        ({ anchor }) =>
          anchor.blockId === props.blockId &&
          (anchor.conversationId ?? null) === props.conversationId,
      ),
      props.walkthrough,
    ),
  );
  const cardsAt = (place: string): JSX.Element => (
    <For each={placed().filter((question) => question.place === place)}>
      {(question) => <WalkthroughQuestion placed={question} session={props.session} />}
    </For>
  );
  const codeQuestions = (stopId: string, index: number, code: WalkthroughCode): QuestionCard[] =>
    placed().flatMap((question): QuestionCard[] => {
      const line = question.moved ? null : codeLine(question.place, stopId, index);
      return line === null || line < code.from || line > code.to
        ? []
        : [
            {
              key: `question:${question.question.entry.conversationId}`,
              line,
              quote: question.question.anchor.quote,
              render: () => <WalkthroughQuestion placed={question} session={props.session} />,
            },
          ];
    });

  const label = (place: string): string => {
    const index = stops().findIndex((stop) => place.startsWith(`${stop.id}/`));
    const stop = stops()[index];
    return `Walkthrough "${props.title}"${stop === undefined ? "" : ` · stop ${index + 1}, ${stop.title}`}`;
  };
  const selection = createWalkthroughSelection(props.session, (place, quote) => ({
    conversationId: props.conversationId,
    blockId: props.blockId,
    place,
    label: label(place),
    quote,
  }));

  const context: StopContext = {
    session: props.session,
    get files() {
      return props.walkthrough.files;
    },
    source: createSources(props.session, props.conversationId, props.blockId),
    open: createBlockModels(props.session, props.scope, () => props.walkthrough.root),
    questionsAt: cardsAt,
    codeQuestions,
    openFolds: () => new Set(props.view().openFolds),
    toggleFold: (key) => {
      const folds = new Set(props.view().openFolds);
      if (!folds.delete(key)) folds.add(key);
      props.setView({ openFolds: [...folds] });
    },
    revealed: (stopId) => props.view().revealed.includes(stopId),
    reveal: (stopId) => props.setView({ revealed: [...props.view().revealed, stopId] }),
    actions: selection.actions,
    onCodeSelect: selection.offer,
  };

  const scroller = (): HTMLElement => root.closest<HTMLElement>(".editor-agent-view") ?? root;
  // A stop or card scrolled to stays at the top while the blocks above it mount and grow, until the reader
  // scrolls themselves.
  let pinned: Element | null = null;
  const place = (element: Element, behavior: ScrollBehavior): void => {
    const area = scroller();
    const top =
      area.scrollTop + element.getBoundingClientRect().top - area.getBoundingClientRect().top;
    area.scrollTo({ top: top - 8, behavior });
  };
  const reveal = (element: Element | null | undefined): void => {
    if (element == null) return;
    pinned = element;
    place(element, "smooth");
  };
  onMount(() => {
    const area = scroller();
    const unpin = (): void => {
      pinned = null;
    };
    const resized = new ResizeObserver(() => {
      if (pinned !== null) place(pinned, "instant");
    });
    resized.observe(root);
    for (const type of ["wheel", "touchstart", "keydown"]) area.addEventListener(type, unpin);
    onCleanup(() => {
      resized.disconnect();
      for (const type of ["wheel", "touchstart", "keydown"]) area.removeEventListener(type, unpin);
    });
  });
  const sections = (): HTMLElement[] => [...root.querySelectorAll<HTMLElement>("[data-stop]")];
  const go = (index: number): void => reveal(sections()[index]);
  const current = trackCurrentStop(
    scroller,
    sections,
    () => stops().length,
    (index) => {
      const id = stops()[index]?.id;
      if (id !== undefined && id !== props.view().stopId) {
        props.setView({ stopId: id, read: [...new Set([...props.view().read, id])] });
      }
    },
  );
  ownStopCommands(() => root, go, current);
  highlightQuotes(() => root, placed);

  onMount(() => {
    onCleanup(selection.listen(root));
    const index = stops().findIndex((stop) => stop.id === props.view().stopId);
    if (index > 0) sections()[index]?.scrollIntoView();
  });
  // A stop or a question picked on the pane's card.
  createEffect(() => {
    const target = requestedTarget(props.session, props.path, true);
    if (target === null) return;
    queueMicrotask(() => {
      if ("stopId" in target) return go(stops().findIndex((stop) => stop.id === target.stopId));
      // A card under code exists once its block's editor mounts; until then its stop is where it is.
      const card = root.querySelector(
        `[data-walkthrough-question="${CSS.escape(target.question)}"]`,
      );
      const place = placed().find(
        (question) => question.question.entry.conversationId === target.question,
      )?.place;
      reveal(
        card ??
          (place === undefined
            ? null
            : root.querySelector(`[data-stop="${CSS.escape(place.split("/")[0]!)}"]`)),
      );
    });
  });

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
      <WalkthroughStrip stops={stops()} current={current()} read={props.view().read} go={go} />
    </div>
  );
}
