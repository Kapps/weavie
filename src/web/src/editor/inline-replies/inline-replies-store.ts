import { createEffect, createRoot, createSignal } from "solid-js";
import { submissionReply } from "../../agent/inline-reply";
import { agentPaneModel } from "../../agent/pane-store";
import type { ClientSession } from "../../bridge";
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
      const remove = (gone: (reply: InlineReply) => boolean): void => {
        const leaving = list().filter(gone);
        if (leaving.length === 0) return;
        for (const { model, mark } of leaving)
          if (!model.isDisposed()) model.deltaDecorations([mark], []);
        setList((current) => current.filter((reply) => !leaving.includes(reply)));
      };
      const offState = session
        .feature("agent")
        .on<{ id: string; status: string; error: string }>("submissionState", (message) => {
          if (message.status !== "rejected") return;
          setList((current) =>
            current.map((reply) =>
              reply.id === message.id ? { ...reply, error: message.error } : reply,
            ),
          );
        });
      // A prompt that left the transcript was cleared or rewound away, and its reply went with it.
      const delivered = new Set<string>();
      createEffect(() => {
        const pane = agentPaneModel(session);
        if (pane === null || !pane.historyComplete() || list().length === 0) return;
        const snapshot = pane.transcript();
        remove((reply) => {
          if (submissionReply(snapshot, reply.id) === null) return delivered.delete(reply.id);
          delivered.add(reply.id);
          return false;
        });
      });
      return {
        list,
        remove,
        add: (reply: InlineReply) => setList((current) => [...current, reply]),
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
        options: { stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges },
      },
    ],
  );
  state.add({ id, model, mark: mark!, question, error: null });
  model.onWillDispose(() => state.remove((reply) => reply.model === model));
}

export function dismissInlineReply(session: ClientSession, id: string): void {
  owned.get(session)?.remove((reply) => reply.id === id);
}

registerCommand(CommandIds.dismissInlineReplies, (_args, { session }) => {
  if (session === null) return false;
  owned.get(session)?.remove(() => true);
});
