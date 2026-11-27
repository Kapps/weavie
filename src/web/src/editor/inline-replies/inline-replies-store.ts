import { getService, ITextModelService } from "@codingame/monaco-vscode-api/services";
import { createEffect, createMemo, createRoot, createSignal } from "solid-js";
import { ownedTarget } from "../../agent/agent-command-owner";
import type { SubmissionState } from "../../agent/composer-state";
import {
  clearReplyComposer,
  followUpComposer,
  type ReplyComposer,
} from "../../agent/composer-store";
import { agentPaneModel } from "../../agent/pane-store";
import type { AgentAnchor, ClientSession } from "../../bridge";
import { type CommandHandler, registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { onScreenContext } from "../../messaging/on-screen";
import { createSessionOwnedResource } from "../../messaging/session-owned-state";
import { notify } from "../../notify/notify";
import { editorContexts } from "../editor-context";
import { monaco } from "../monaco-setup";
import { anchorPath, selectionExcerpt } from "../selection-excerpt";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "../session-uri";

/** An anchor in a file, which an inline reply has. */
export type FileAnchor = AgentAnchor & { path: string };
type CardState = "expanded" | "collapsed" | "dismissed";

/** A prompt sent from a selection in a file, answered under the code it was asked about. */
export interface InlineReply {
  /** Its first prompt's submission id, which every prompt of the card names as its anchor's card. */
  readonly id: string;
  readonly anchor: FileAnchor;
  readonly question: string;
  /** The submissions asked from this card after the first, oldest first. */
  readonly followUps: readonly string[];
  /** Why the host refused the prompt. */
  readonly error: string | null;
  readonly collapsed: boolean;
}

/** Where a reply sits in a model: under `line`, `moved` once the code it was asked about can't be found. */
export interface ReplyPlace {
  line: number;
  moved: boolean;
}

/** An anchor's place for `range` of a file: `line.column-line.column`. */
export const anchorPlace = (range: monaco.IRange): string =>
  `${range.startLineNumber}.${range.startColumn}-${range.endLineNumber}.${range.endColumn}`;

const anchorRange = (anchor: AgentAnchor): monaco.Range => {
  const [from, to] = anchor.place.split("-").map((end) => end.split(".").map(Number)) as [
    [number, number],
    [number, number],
  ];
  return new monaco.Range(from[0], from[1], to[0], to[1]);
};

/**
 * Where `anchor`'s code is in `model`: its range while the range's last line holds the quote, else the range moved
 * to the nearest line that does; null once none does.
 */
export function locateAnchor(
  model: monaco.editor.ITextModel,
  anchor: AgentAnchor,
): monaco.IRange | null {
  const range = anchorRange(anchor);
  const count = model.getLineCount();
  const holds = (line: number): boolean =>
    line >= 1 && line <= count && model.getLineContent(line).includes(anchor.quote);
  for (let away = 0; away < count; away++) {
    const line = [range.endLineNumber - away, range.endLineNumber + away].find(holds);
    if (line !== undefined) return model.validateRange(range.delta(line - range.endLineNumber));
  }
  return null;
}

const composerKey = (id: string): string => `inline-reply:${id}`;

const EMPTY: readonly InlineReply[] = [];

// Keeps a file's working copy open with no tab or review showing it, so its marks follow edits exactly; a model of
// any other kind is its opener's.
function holdOpen(model: monaco.editor.ITextModel): () => void {
  if (model.uri.scheme !== SESSION_FILE_SCHEME) return () => {};
  const reference = getService(ITextModelService).then((service) =>
    service.createModelReference(model.uri),
  );
  return () => void reference.then((held) => held.dispose());
}

const sameReplies = (a: readonly InlineReply[], b: readonly InlineReply[]): boolean =>
  a.length === b.length &&
  a.every((reply, index) => {
    const other = b[index]!;
    return (
      reply.id === other.id &&
      reply.question === other.question &&
      reply.error === other.error &&
      reply.collapsed === other.collapsed &&
      reply.followUps.join() === other.followUps.join()
    );
  });

const owned = createSessionOwnedResource(
  (session) =>
    createRoot((dispose) => {
      // Cards sent from this page whose first prompt the transcript doesn't hold yet, and the follow-ups on their way.
      const [sent, setSent] = createSignal<readonly InlineReply[]>(EMPTY);
      const [asked, setAsked] = createSignal<ReadonlyMap<string, readonly string[]>>(new Map());
      // What the user made of a card that the transcript doesn't show yet: it shows at once while the host records
      // it, until the transcript moves off the state it had then (`from`) to this one.
      const [intents, setIntents] = createSignal<
        ReadonlyMap<string, { state: CardState; from: CardState }>
      >(new Map());
      // The cards whose prompts the transcript holds; a transcript reloading keeps the last it had.
      const saved = createMemo<readonly InlineReply[]>(
        (previous) => {
          const pane = agentPaneModel(session);
          if (pane === null || !pane.historyComplete()) return previous;
          const cards = new Map<
            string,
            InlineReply & { followUps: string[]; dismissed: boolean }
          >();
          for (const entry of pane.transcript().entries) {
            const { anchor, submissionId } = entry;
            if (anchor?.path == null || submissionId === undefined) continue;
            if (submissionId === anchor.card)
              cards.set(submissionId, {
                id: submissionId,
                anchor: anchor as FileAnchor,
                question: entry.text ?? "",
                followUps: [],
                error: null,
                collapsed: entry.replyCard === "collapsed",
                dismissed: entry.replyCard === "dismissed",
              });
            else cards.get(anchor.card)?.followUps.push(submissionId);
          }
          return [...cards.values()].filter((card) => !card.dismissed);
        },
        EMPTY,
        { equals: sameReplies },
      );
      const list = createMemo(
        (): readonly InlineReply[] => {
          const waiting = asked();
          const known = new Set(saved().map((reply) => reply.id));
          return [...saved(), ...sent().filter((reply) => !known.has(reply.id))].flatMap(
            (reply) => {
              const state = intents().get(reply.id)?.state;
              return state === "dismissed"
                ? []
                : [
                    {
                      ...reply,
                      followUps: [...reply.followUps, ...(waiting.get(reply.id) ?? [])],
                      collapsed: state === undefined ? reply.collapsed : state === "collapsed",
                    },
                  ];
            },
          );
        },
        EMPTY,
        { equals: sameReplies },
      );
      // Prompts the transcript now holds are its to keep: a card or follow-up later cleared or rewound away is gone.
      createEffect(() => {
        const landed = new Set(saved().flatMap((reply) => [reply.id, ...reply.followUps]));
        if (sent().some((reply) => landed.has(reply.id)))
          setSent((current) => current.filter((reply) => !landed.has(reply.id)));
        if ([...asked().values()].some((ids) => ids.some((id) => landed.has(id))))
          setAsked((current) => withoutFollowUps(current, landed));
        const now = (id: string): CardState => savedState(saved(), id);
        if ([...intents()].some(([id, { from }]) => now(id) !== from))
          setIntents(
            (current) =>
              new Map(
                [...current].flatMap(([id, intent]) =>
                  now(id) === intent.from
                    ? [[id, intent] as const]
                    : now(id) === intent.state
                      ? []
                      : [[id, { state: intent.state, from: now(id) }] as const],
                ),
              ),
          );
      });

      // Each reply's mark in the model it was last shown in, and each such model's listeners.
      const marks = new Map<
        string,
        { model: monaco.editor.ITextModel; mark: string; moved: boolean }
      >();
      const watched = new Map<monaco.editor.ITextModel, () => void>();
      const unmark = (gone: (id: string) => boolean): void => {
        for (const [id, { model, mark }] of [...marks]) {
          if (!gone(id)) continue;
          marks.delete(id);
          if (!model.isDisposed()) model.deltaDecorations([mark], []);
          if ([...marks.values()].some((other) => other.model === model)) continue;
          watched.get(model)?.();
          watched.delete(model);
        }
      };
      const watch = (model: monaco.editor.ITextModel): void => {
        if (watched.has(model)) return;
        const ofModel = (id: string): boolean => marks.get(id)?.model === model;
        const subscriptions = [
          { dispose: holdOpen(model) },
          model.onWillDispose(() => unmark(ofModel)),
          // Replacing the whole text discards the marks, so each reply finds its code again.
          model.onDidChangeContent((event) => {
            if (event.isFlush) unmark(ofModel);
          }),
        ];
        watched.set(model, () => {
          for (const subscription of subscriptions) subscription.dispose();
        });
      };
      const mark = (
        id: string,
        model: monaco.editor.ITextModel,
        range: monaco.IRange,
        moved: boolean,
      ) => {
        unmark((other) => other === id);
        const [decoration] = model.deltaDecorations(
          [],
          [
            {
              range,
              // Text typed at the code's end, a new line included, goes after the mark.
              options: {
                stickiness: monaco.editor.TrackedRangeStickiness.GrowsOnlyWhenTypingBefore,
              },
            },
          ],
        );
        marks.set(id, { model, mark: decoration!, moved });
        watch(model);
      };
      // A reply that left drops its mark and its composer.
      let shown = new Set<string>();
      createEffect(() => {
        const ids = new Set(list().map((reply) => reply.id));
        for (const id of shown) if (!ids.has(id)) clearReplyComposer(session, composerKey(id));
        unmark((id) => !ids.has(id));
        shown = ids;
      });

      const offState = session
        .feature("agent")
        .on<SubmissionState>("submissionState", ({ id, status, error }) => {
          if (status === "accepted") return;
          // A follow-up that went nowhere leaves the thread; its box holds a refusal's reason.
          if ([...asked().values()].some((ids) => ids.includes(id)))
            setAsked((current) => withoutFollowUps(current, new Set([id])));
          else if (status === "dropped")
            setSent((current) => current.filter((reply) => reply.id !== id));
          else
            setSent((current) =>
              current.map((reply) => (reply.id === id ? { ...reply, error } : reply)),
            );
        });
      // A refused card never reached the host; any other is recorded there, which holds it wherever its prompt lands.
      const intend = (gone: readonly InlineReply[], state: CardState): void => {
        for (const reply of gone)
          if (reply.error === null)
            session.feature("agent").publish("setReplyCard", { submissionId: reply.id, state });
        setIntents(
          (current) =>
            new Map([
              ...current,
              ...gone.map(
                (reply) => [reply.id, { state, from: savedState(saved(), reply.id) }] as const,
              ),
            ]),
        );
      };
      return {
        list,
        marks,
        mark,
        addSent: (reply: InlineReply) => setSent((current) => [...current, reply]),
        addFollowUp: (id: string, followUp: string) =>
          setAsked(
            (current) => new Map([...current, [id, [...(current.get(id) ?? []), followUp]]]),
          ),
        setCollapsed: (id: string, collapsed: boolean) =>
          intend(
            list().filter((reply) => reply.id === id),
            collapsed ? "collapsed" : "expanded",
          ),
        dismiss: (gone: (reply: InlineReply) => boolean) => {
          intend(list().filter(gone), "dismissed");
          setSent((current) => current.filter((reply) => !gone(reply)));
        },
        dispose: () => {
          offState();
          unmark(() => true);
          dispose();
        },
      };
    }),
  (_session, state) => state.dispose(),
);

// A card's state as the transcript has it; one it doesn't hold reads as dismissed.
function savedState(saved: readonly InlineReply[], id: string): CardState {
  const card = saved.find((reply) => reply.id === id);
  return card === undefined ? "dismissed" : card.collapsed ? "collapsed" : "expanded";
}

function withoutFollowUps(
  asked: ReadonlyMap<string, readonly string[]>,
  gone: ReadonlySet<string>,
): ReadonlyMap<string, readonly string[]> {
  return new Map(
    [...asked]
      .map(([id, ids]) => [id, ids.filter((other) => !gone.has(other))] as const)
      .filter(([, ids]) => ids.length > 0),
  );
}

/** The session's inline replies, oldest first. */
export const inlineReplies = (session: ClientSession | null): readonly InlineReply[] =>
  owned.get(session)?.list() ?? EMPTY;

/** Whether `reply` belongs in `model`. */
export const replyIn = (reply: InlineReply, model: monaco.editor.ITextModel): boolean =>
  reply.anchor.path === anchorPath(model);

/**
 * Where `reply` sits in `model`, which shows its file: under its mark, which is made the first time, at its code as
 * found then, or where that code was once it can't be found.
 */
export function placeReply(
  session: ClientSession,
  reply: InlineReply,
  model: monaco.editor.ITextModel,
): ReplyPlace | null {
  const state = owned.get(session);
  if (state === undefined) return null;
  const held = state.marks.get(reply.id);
  const range = held?.model === model ? model.getDecorationRange(held.mark) : null;
  if (range !== null) return { line: range.endLineNumber, moved: held!.moved };
  const found = locateAnchor(model, reply.anchor);
  const code = found ?? model.validateRange(anchorRange(reply.anchor));
  state.mark(reply.id, model, code, found === null);
  return { line: code.endLineNumber, moved: found === null };
}

/** Shows the reply to submission `anchor.card` under `code` in `model`, until it is dismissed. */
export function addInlineReply(
  session: ClientSession,
  model: monaco.editor.ITextModel,
  code: monaco.IRange,
  anchor: FileAnchor,
  question: string,
): void {
  const state = owned.get(session);
  if (state === undefined) return;
  state.mark(anchor.card, model, code, false);
  state.addSent({
    id: anchor.card,
    anchor,
    question,
    followUps: [],
    error: null,
    collapsed: false,
  });
}

export function setInlineReplyCollapsed(
  session: ClientSession,
  id: string,
  collapsed: boolean,
): void {
  owned.get(session)?.setCollapsed(id, collapsed);
}

export function dismissInlineReply(session: ClientSession, id: string): void {
  owned.get(session)?.dismiss((reply) => reply.id === id);
}

/** Ends the replies on a file that is gone, which nothing can show again. */
export function dismissInlineRepliesOn(session: ClientSession, uri: monaco.Uri): void {
  const path = sessionUriHostPath(uri);
  owned.get(session)?.dismiss((reply) => reply.anchor.path === path);
}

// The code `reply` is about where it is shown, or null while it isn't shown or once it has been deleted.
function codeOf(session: ClientSession, reply: InlineReply) {
  const held = owned.get(session)?.marks.get(reply.id);
  const range = held === undefined ? null : held.model.getDecorationRange(held.mark);
  return range === null || range.isEmpty() ? null : { model: held!.model, range };
}

/** The composer of `reply`'s follow-up box, whose prompts join the card. */
export const inlineFollowUp = (session: ClientSession, reply: InlineReply): ReplyComposer =>
  followUpComposer(
    session,
    composerKey(reply.id),
    reply.anchor,
    () => {
      const code = codeOf(session, reply);
      return code === null ? null : selectionExcerpt(code.model, code.range);
    },
    (followUp) => owned.get(session)?.addFollowUp(reply.id, followUp),
  );

/** The context key held while a card owns the card commands; its target is that reply's id. */
export const INLINE_REPLY_OWNED = "agentInlineReplyOwned";

// The reply a command names by `id`, else the card the user last pressed or focused, else the one in view
// nearest the cursor in the session's editor.
function target(session: ClientSession, args: unknown): InlineReply | undefined {
  const replies = inlineReplies(session);
  const id = (args as { id?: unknown } | null | undefined)?.id;
  if (id !== undefined) {
    const named = replies.find((reply) => reply.id === id);
    if (named === undefined) throw new Error(`There is no inline reply '${String(id)}'.`);
    return named;
  }
  const owned = replies.find((reply) => reply.id === ownedTarget<string>(INLINE_REPLY_OWNED));
  if (owned !== undefined) return owned;
  const connection = editorContexts.get(session);
  const cursor = connection?.editor.getPosition()?.lineNumber;
  if (connection === undefined || cursor === undefined) return undefined;
  const inView = (line: number): boolean =>
    connection.editor
      .getVisibleRanges()
      .some((range) => range.startLineNumber <= line && line <= range.endLineNumber);
  return replies
    .flatMap((reply) => {
      const code = codeOf(session, reply);
      const line = code?.model === connection.model ? code.range.endLineNumber : undefined;
      return line !== undefined && inView(line) ? [{ reply, away: Math.abs(line - cursor) }] : [];
    })
    .sort((a, b) => a.away - b.away)[0]?.reply;
}

const onTarget =
  (act: (session: ClientSession, reply: InlineReply) => void): CommandHandler =>
  (args, { session }) => {
    const reply = session === null ? undefined : target(session, args);
    if (session === null || reply === undefined) {
      notify("info", "There's no inline reply in view.", "inline-replies");
      return;
    }
    act(session, reply);
  };

registerCommand(CommandIds.dismissInlineReplies, (_args, { session }) => {
  if (session === null) return false;
  owned.get(session)?.dismiss(() => true);
});
registerCommand(
  CommandIds.dismissInlineReply,
  onTarget((session, reply) => dismissInlineReply(session, reply.id)),
);
registerCommand(
  CommandIds.toggleInlineReply,
  onTarget((session, reply) => setInlineReplyCollapsed(session, reply.id, !reply.collapsed)),
);
registerCommand(
  CommandIds.replyToInlineReply,
  onTarget((session, reply) => {
    setInlineReplyCollapsed(session, reply.id, false);
    inlineFollowUp(session, reply).setOpen(true);
    // The box is in the card once the state lands; the editor showing it is the one to type in.
    editorContexts
      .get(session)
      ?.editor.getContainerDomNode()
      .querySelector<HTMLElement>(
        `[data-inline-reply="${reply.id}"] [data-inline-follow-up] textarea`,
      )
      ?.focus();
  }),
);

onScreenContext("agentInlineReplies", (session) => inlineReplies(session).length > 0);
