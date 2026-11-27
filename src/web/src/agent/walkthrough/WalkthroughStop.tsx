import { createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import type {
  ClientSession,
  WalkthroughStop as Stop,
  WalkthroughCode,
  WalkthroughFile,
} from "../../bridge";
import { AgentMarkdown } from "../AgentMarkdown";
import type { SelectionActions } from "../selection-actions";
import { type BlockModel, createWalkthroughCode, type QuestionCard } from "./walkthrough-code";
import { codePlace } from "./walkthrough-questions";

const EFFECTS = { changed: "changed", affected: "plays out differently", added: "new" } as const;

/** What an update did to a stop, as a tag; nothing for a stop no update touched. */
export function EffectTag(props: { effect: Stop["effect"] }): JSX.Element {
  return (
    <Show when={props.effect}>
      {(effect) => <span class={`walkthrough-tag is-${effect()}`}>{EFFECTS[effect()]}</span>}
    </Show>
  );
}

/** Markdown the reader can select and ask about: `place` names it, and its questions follow it. */
export function Prose(props: {
  session: ClientSession;
  place: string;
  content: string;
  class: string;
  after: JSX.Element;
}): JSX.Element {
  // One key for the prose's life; the render cache compares content itself.
  const key = {};
  return (
    <>
      <div class={`walkthrough-prose ${props.class}`} data-place={props.place}>
        <AgentMarkdown
          attachable={false}
          cacheKey={key}
          content={props.content}
          flavor="agent"
          renderMermaid={true}
          session={props.session}
        />
      </div>
      {props.after}
    </>
  );
}

export interface StopContext {
  session: ClientSession;
  files: readonly WalkthroughFile[];
  source: (hash: string) => Promise<string>;
  open: (code: WalkthroughCode, copy: string) => Promise<BlockModel>;
  /** The questions asked at `place`, as cards. */
  questionsAt: (place: string) => JSX.Element;
  /** The question cards for code block `index` of stop `stopId`, by the copy's line. */
  codeQuestions: (stopId: string, index: number, code: WalkthroughCode) => QuestionCard[];
  openFolds: () => ReadonlySet<string>;
  toggleFold: (key: string) => void;
  revealed: (stopId: string) => boolean;
  reveal: (stopId: string) => void;
  actions: Pick<SelectionActions, "clear" | "moved">;
  onCodeSelect: (
    place: string,
    text: string,
    quote: string,
    point: { x: number; y: number },
  ) => void;
}

/** One stop: its heading, lead, code, the example after it, why, and a prediction. */
export function WalkthroughStop(props: {
  stop: Stop;
  index: number;
  count: number;
  context: StopContext;
}): JSX.Element {
  const place = (part: string) => `${props.stop.id}/${part}`;
  const prose = (part: string, content: string, cls: string) => (
    <Prose
      session={props.context.session}
      place={place(part)}
      content={content}
      class={cls}
      after={props.context.questionsAt(place(part))}
    />
  );
  return (
    <section class="walkthrough-stop" data-stop={props.stop.id}>
      <div class="walkthrough-stop-number">
        Stop {props.index + 1} of {props.count}
      </div>
      <h2 class="walkthrough-stop-title">{props.stop.title}</h2>
      <Show when={props.stop.effect}>
        <p class="walkthrough-changed">
          <EffectTag effect={props.stop.effect ?? null} />
          {props.stop.note}
        </p>
      </Show>
      {prose("lead", props.stop.lead, "walkthrough-lead")}
      <For each={props.stop.code}>
        {(code, index) => (
          <CodeBlock code={code} index={index()} stopId={props.stop.id} context={props.context} />
        )}
      </For>
      <Show when={props.stop.state}>
        {(state) => (
          <>
            <div class="walkthrough-state" data-place={place("state")}>
              <div class="ui-section-label">After this stop</div>
              <div class="walkthrough-state-cells">
                <For each={state().cells}>
                  {(cell) => (
                    <div>
                      <div class="walkthrough-state-label">{cell.label}</div>
                      <div class="walkthrough-state-value">
                        {cell.value === "" ? "nothing" : cell.value}
                      </div>
                    </div>
                  )}
                </For>
              </div>
              <Show when={state().note}>
                {(note) => <p class="walkthrough-state-note">{note()}</p>}
              </Show>
            </div>
            {props.context.questionsAt(place("state"))}
          </>
        )}
      </Show>
      <Show when={props.stop.why}>
        {(why) => (
          <aside class="walkthrough-why">
            <div class="walkthrough-why-title">{why().title}</div>
            {prose("why", why().text, "")}
            <div class="walkthrough-source">
              {why().source.kind === "reading"
                ? "The agent's own reading of the code"
                : `Source: ${why().source.ref}`}
            </div>
          </aside>
        )}
      </Show>
      <Show when={props.stop.predict}>
        {(predict) => (
          <div class="walkthrough-predict" data-place={place("predict")}>
            <div class="ui-section-label">Before you read on</div>
            <p>{predict().question}</p>
            <Show
              when={props.context.revealed(props.stop.id)}
              fallback={
                <button
                  type="button"
                  class="ui-button"
                  onClick={() => props.context.reveal(props.stop.id)}
                >
                  Show the Answer
                </button>
              }
            >
              <p class="walkthrough-answer">{predict().answer}</p>
            </Show>
          </div>
        )}
      </Show>
    </section>
  );
}

// A block becomes an editor only as it nears the screen; until then it holds its place at its lines' height.
function CodeBlock(props: {
  code: WalkthroughCode;
  index: number;
  stopId: string;
  context: StopContext;
}): JSX.Element {
  let mount!: HTMLDivElement;
  let frame!: HTMLDivElement;
  const [notices, setNotices] = createSignal<readonly string[]>([]);
  const foldKey = (from: number) => `${props.stopId}/${props.index}/${from}`;
  onMount(() => {
    let dispose: (() => void) | null = null;
    let disposed = false;
    const observer = new IntersectionObserver(
      (records) => {
        if (!records.some((record) => record.isIntersecting) || dispose !== null) return;
        observer.disconnect();
        const cited = props.context.files.find((file) => file.path === props.code.path);
        if (cited === undefined) return;
        void createWalkthroughCode({
          mount,
          code: props.code,
          file: cited,
          source: props.context.source,
          open: props.context.open,
          openFolds: () =>
            new Set(
              (props.code.folds ?? [])
                .filter((fold) => props.context.openFolds().has(foldKey(fold.from)))
                .map((fold) => fold.from),
            ),
          toggleFold: (from) => props.context.toggleFold(foldKey(from)),
          questions: () => props.context.codeQuestions(props.stopId, props.index, props.code),
          actions: props.context.actions,
          onSelect: (text, quote, line, point) =>
            props.context.onCodeSelect(
              codePlace(props.stopId, props.index, props.code, line),
              text,
              quote,
              point,
            ),
          onNotices: setNotices,
        }).then(
          (release) => {
            if (disposed) release();
            else dispose = release;
          },
          (error: unknown) =>
            setNotices([
              error instanceof Error ? error.message : `Couldn't show this code: ${String(error)}`,
            ]),
        );
      },
      // The tab scrolls, not the window, so a block out of its view is clipped from the window's.
      { root: frame.closest(".editor-agent-view"), rootMargin: "100% 0px" },
    );
    observer.observe(frame);
    onCleanup(() => {
      disposed = true;
      observer.disconnect();
      dispose?.();
    });
  });
  const parts = () => props.code.path.split("/");
  return (
    <div class="walkthrough-code" ref={frame}>
      <div class="walkthrough-file" title={props.code.path}>
        <span class="walkthrough-file-dir">{parts().slice(0, -1).join("/")}/</span>
        {parts().at(-1)}
        <span class="walkthrough-file-lines">
          {props.code.from}–{props.code.to}
        </span>
      </div>
      <For each={notices()}>{(notice) => <div class="walkthrough-notice">{notice}</div>}</For>
      <div
        class="walkthrough-editor"
        ref={mount}
        style={{ height: `calc(${shownLines(props.code)} * var(--font-content-row-line))` }}
      />
    </div>
  );
}

const shownLines = (code: WalkthroughCode): number =>
  code.to - code.from + 1 - (code.folds ?? []).reduce((sum, fold) => sum + fold.to - fold.from, 0);
