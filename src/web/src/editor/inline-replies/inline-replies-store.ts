import { createEffect, createRoot, createSignal } from "solid-js";
import { ownedTarget } from "../../agent/agent-command-owner";
import type { SubmissionState } from "../../agent/composer-state";
import {
  clearReplyComposer,
  followUpComposer,
  type ReplyComposer,
} from "../../agent/composer-store";
import { hasSubmission } from "../../agent/inline-reply";
import { agentPaneModel } from "../../agent/pane-store";
import { type ClientSession, selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { type CommandHandler, registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { createSessionOwnedResource } from "../../messaging/session-owned-state";
import { notify } from "../../notify/notify";
import { editorContexts } from "../editor-context";
import { monaco } from "../monaco-setup";
import { lastLineOf, selectionExcerpt } from "../selection-excerpt";

/** A prompt sent from an editor selection, answered under the line it was sent from. */
export interface InlineReply {
  /** The submission's id, which its prompt carries in the transcript. */
  readonly id: string;
  readonly model: monaco.editor.ITextModel;
  /** The model decoration that follows the code the question is about; the reply sits under its last line. */
  readonly mark: string;
  readonly question: string;
  /** The submissions asked from this card after the first, oldest first. */
  readonly followUps: readonly string[];
  /** Why the host refused the prompt. */
  readonly error: string | null;
  readonly collapsed: boolean;
}

const composerKey = (id: string): string => `inline-reply:${id}`;

const EMPTY: readonly InlineReply[] = [];

const owned = createSessionOwnedResource(
  (session) =>
    createRoot((dispose) => {
      const [list, setList] = createSignal(EMPTY);
      // Prompts seen in the transcript, and each model's listeners while it holds a reply.
      const delivered = new Set<string>();
      const watched = new Map<monaco.editor.ITextModel, () => void>();
      const remove = (gone: (reply: InlineReply) => boolean): void => {
        const leaving = list().filter(gone);
        if (leaving.length === 0) return;
        const kept = list().filter((reply) => !leaving.includes(reply));
        // The cards unmount first: a mounted one would take a new composer as its old one is cleared.
        setList(kept);
        for (const { id, followUps, model, mark } of leaving) {
          for (const prompt of [id, ...followUps]) delivered.delete(prompt);
          clearReplyComposer(session, composerKey(id));
          if (!model.isDisposed()) model.deltaDecorations([mark], []);
          if (kept.some((reply) => reply.model === model)) continue;
          watched.get(model)?.();
          watched.delete(model);
        }
      };
      const add = (reply: InlineReply): void => {
        const { model } = reply;
        if (!watched.has(model)) {
          const ofModel = (other: InlineReply): boolean => other.model === model;
          const subscriptions = [
            model.onWillDispose(() => remove(ofModel)),
            // Replacing the whole text discards the marks that place its replies.
            model.onDidChangeContent((event) => {
              if (event.isFlush) remove(ofModel);
            }),
          ];
          watched.set(model, () => {
            for (const subscription of subscriptions) subscription.dispose();
          });
        }
        setList((current) => [...current, reply]);
      };
      const patch = (id: string, change: (reply: InlineReply) => Partial<InlineReply>): void => {
        if (list().some((reply) => reply.id === id))
          setList((current) =>
            current.map((reply) => (reply.id === id ? { ...reply, ...change(reply) } : reply)),
          );
      };
      const offState = session
        .feature("agent")
        .on<SubmissionState>("submissionState", ({ id, status, error }) => {
          if (status === "accepted") return;
          // A follow-up that went nowhere leaves the thread; its box holds a refusal's reason.
          const asked = list().find((reply) => reply.followUps.includes(id));
          if (asked !== undefined)
            patch(asked.id, (reply) => ({
              followUps: reply.followUps.filter((other) => other !== id),
            }));
          else if (status === "dropped") remove((reply) => reply.id === id);
          else patch(id, () => ({ error }));
        });
      // A prompt that left the transcript was cleared or rewound away: a follow-up leaves its card, and a
      // card goes with its first prompt.
      createEffect(() => {
        const pane = agentPaneModel(session);
        if (pane === null || !pane.historyComplete() || list().length === 0) return;
        const snapshot = pane.transcript();
        const gone = (id: string): boolean => {
          if (!hasSubmission(snapshot, id)) return delivered.has(id);
          delivered.add(id);
          return false;
        };
        for (const { id, followUps } of list()) {
          const lost = followUps.filter(gone);
          for (const prompt of lost) delivered.delete(prompt);
          if (lost.length > 0)
            patch(id, (reply) => ({
              followUps: reply.followUps.filter((prompt) => !lost.includes(prompt)),
            }));
        }
        remove((reply) => gone(reply.id));
      });
      return {
        list,
        add,
        patch,
        remove,
        dispose: () => {
          offState();
          remove(() => true);
          dispose();
        },
      };
    }),
  (_session, state) => state.dispose(),
);

/** The session's inline replies, oldest first. */
export const inlineReplies = (session: ClientSession | null): readonly InlineReply[] =>
  owned.get(session)?.list() ?? EMPTY;

/** The code `reply` is about as it stands now, or null once it has been deleted. */
function codeOf(reply: InlineReply): monaco.Range | null {
  const range = reply.model.getDecorationRange(reply.mark);
  return range === null || range.isEmpty() ? null : range;
}

/** The line `reply` sits under, or undefined when its mark is gone. */
export function inlineReplyLine(reply: InlineReply): number | undefined {
  const range = reply.model.getDecorationRange(reply.mark);
  return range === null ? undefined : lastLineOf(range);
}

/** Shows the reply to submission `id` under `code` in `model`, for as long as the model lives. */
export function addInlineReply(
  session: ClientSession,
  model: monaco.editor.ITextModel,
  code: monaco.IRange,
  id: string,
  question: string,
): void {
  const state = owned.get(session);
  if (state === undefined) return;
  const [mark] = model.deltaDecorations(
    [],
    [
      {
        range: code,
        // Text typed at the code's end, a new line included, goes after the mark.
        options: { stickiness: monaco.editor.TrackedRangeStickiness.GrowsOnlyWhenTypingBefore },
      },
    ],
  );
  state.add({ id, model, mark: mark!, question, followUps: [], error: null, collapsed: false });
}

export function setInlineReplyCollapsed(
  session: ClientSession,
  id: string,
  collapsed: boolean,
): void {
  owned.get(session)?.patch(id, () => ({ collapsed }));
}

export function dismissInlineReply(session: ClientSession, id: string): void {
  owned.get(session)?.remove((reply) => reply.id === id);
}

/** The composer of `reply`'s follow-up box, whose prompts join the card. */
export const inlineFollowUp = (session: ClientSession, reply: InlineReply): ReplyComposer =>
  followUpComposer(
    session,
    composerKey(reply.id),
    () => {
      const code = codeOf(reply);
      return code === null ? null : selectionExcerpt(reply.model, code);
    },
    (followUp) =>
      owned.get(session)?.patch(reply.id, (current) => ({
        followUps: [...current.followUps, followUp],
      })),
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
      const line = reply.model === connection.model ? inlineReplyLine(reply) : undefined;
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
  owned.get(session)?.remove(() => true);
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

createRoot(() =>
  createEffect(() => setContext("agentInlineReplies", inlineReplies(selectedSession()).length > 0)),
);
