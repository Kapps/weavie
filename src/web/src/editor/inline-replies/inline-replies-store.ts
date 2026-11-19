import { createEffect, createRoot, createSignal } from "solid-js";
import type { SubmissionState } from "../../agent/composer-state";
import { hasSubmission } from "../../agent/inline-reply";
import { agentPaneModel } from "../../agent/pane-store";
import { type ClientSession, selectedSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { registerCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { createSessionOwnedResource } from "../../messaging/session-owned-state";
import { monaco } from "../monaco-setup";

/** A prompt sent from an editor selection, answered under the line it was sent from. */
export interface InlineReply {
  /** The submission's id, which its prompt carries in the transcript. */
  readonly id: string;
  readonly model: monaco.editor.ITextModel;
  /** The model decoration that follows the line the reply sits under. */
  readonly mark: string;
  readonly question: string;
  /** Why the host refused the prompt. */
  readonly error: string | null;
}

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
        for (const { id, model, mark } of leaving) {
          delivered.delete(id);
          if (!model.isDisposed()) model.deltaDecorations([mark], []);
          if (kept.some((reply) => reply.model === model)) continue;
          watched.get(model)?.();
          watched.delete(model);
        }
        setList(kept);
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
      const offState = session
        .feature("agent")
        .on<SubmissionState>("submissionState", ({ id, status, error }) => {
          if (status === "dropped") remove((reply) => reply.id === id);
          else if (status === "rejected" && list().some((reply) => reply.id === id))
            setList((current) =>
              current.map((reply) => (reply.id === id ? { ...reply, error } : reply)),
            );
        });
      // A prompt that left the transcript was cleared or rewound away, and its reply went with it.
      createEffect(() => {
        const pane = agentPaneModel(session);
        if (pane === null || !pane.historyComplete() || list().length === 0) return;
        const snapshot = pane.unfolded();
        remove((reply) => {
          if (!hasSubmission(snapshot, reply.id)) return delivered.has(reply.id);
          delivered.add(reply.id);
          return false;
        });
      });
      return {
        list,
        add,
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

/** Shows the reply to submission `id` under `line` of `model`, for as long as the model lives. */
export function addInlineReply(
  session: ClientSession,
  model: monaco.editor.ITextModel,
  line: number,
  id: string,
  question: string,
): void {
  const state = owned.get(session);
  if (state === undefined) return;
  const end = model.getLineMaxColumn(line);
  const [mark] = model.deltaDecorations(
    [],
    [
      {
        range: new monaco.Range(line, end, line, end),
        // Text typed at the line's end, a new line included, goes after the mark.
        options: { stickiness: monaco.editor.TrackedRangeStickiness.GrowsOnlyWhenTypingBefore },
      },
    ],
  );
  state.add({ id, model, mark: mark!, question, error: null });
}

export function dismissInlineReply(session: ClientSession, id: string): void {
  owned.get(session)?.remove((reply) => reply.id === id);
}

registerCommand(CommandIds.dismissInlineReplies, (_args, { session }) => {
  if (session === null) return false;
  owned.get(session)?.remove(() => true);
});

createRoot(() =>
  createEffect(() => setContext("agentInlineReplies", inlineReplies(selectedSession()).length > 0)),
);
