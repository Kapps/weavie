// Shows the session's PR review threads inline in one editor binding, anchored to the live buffer, and hosts the
// new-comment draft for the cursor's line.

import {
  type Accessor,
  createEffect,
  createMemo,
  createResource,
  createRoot,
  createSignal,
  Show,
} from "solid-js";
import type { ClientSession } from "../../bridge";
import { setContext } from "../../commands/context";
import { describeError } from "../../lsp/lsp-errors";
import { notify } from "../../notify/notify";
import { editorContexts } from "../editor-context";
import { samePath } from "../fs-path";
import { monaco } from "../monaco-setup";
import { DIFF_RECOMPUTE_DEBOUNCE_MS } from "../review/diff-zones";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "../session-uri";
import { createZoneCards, type ZoneCard } from "../zone-cards";
import { type AnchorMap, createAnchorMap, type ThreadPlacement } from "./anchor-map";
import { PrDraftCard, PrThreadCard } from "./PrThreadCard";
import {
  type PrSources,
  prCommentsFor,
  prCommentsVisible,
  sendPrComment,
} from "./pr-comments-store";
import "./pr-comments.css";

export interface PrCommentController {
  /** Whether this file is part of the branch's pull request, so a new comment can be left on it. */
  commentable(): boolean;
  /** Opens a comment box on the cursor's line: the reply of the thread anchored there, else a new comment. */
  comment(): boolean;
  dispose(): void;
}

type Card = { kind: "thread"; placement: ThreadPlacement } | { kind: "draft"; line: number };

const inert: PrCommentController = {
  commentable: () => false,
  comment: () => false,
  dispose: () => {},
};

export function createPrComments(
  session: ClientSession,
  editor: monaco.editor.IStandaloneCodeEditor,
  model: monaco.editor.ITextModel,
): PrCommentController {
  if (model.uri.scheme !== SESSION_FILE_SCHEME) return inert;
  const path = sessionUriHostPath(model.uri);
  return createRoot((dispose) => {
    const set = () => prCommentsFor(session)?.set ?? null;
    const commentable = createMemo(
      () => set()?.changedPaths.some((changed) => samePath(changed, path)) ?? false,
    );
    const threads = createMemo(() =>
      prCommentsVisible()
        ? (set()?.threads.filter((thread) => samePath(thread.path, path)) ?? [])
        : [],
    );
    const [sources] = createResource(
      () => {
        const current = set();
        return current !== null && (threads().length > 0 || commentable())
          ? current.headSha
          : false;
      },
      (headSha) =>
        session
          .feature("pullRequests")
          .request<PrSources, { path: string; headSha: string }>("sources", { path, headSha })
          .then((sources) => ({ headSha, sources }))
          .catch((error: unknown) => {
            notify(
              "warn",
              `Couldn't load PR comment lines: ${describeError(error)}`,
              "pr-comments",
            );
            return null;
          }),
    );
    const [version, setVersion] = createSignal(0);
    let recompute: ReturnType<typeof setTimeout> | undefined;
    const onContent = model.onDidChangeContent(() => {
      clearTimeout(recompute);
      recompute = setTimeout(() => setVersion((value) => value + 1), DIFF_RECOMPUTE_DEBOUNCE_MS);
    });
    // The map remembers the head it was built from, so a post can't pair new-head lines with old-head text.
    const anchors = createMemo((): { map: AnchorMap; headSha: string } | null => {
      version();
      const current = sources();
      if (current == null) return null;
      try {
        return {
          map: createAnchorMap(current.sources, model.getValue()),
          headSha: current.headSha,
        };
      } catch (error) {
        notify("warn", describeError(error), "pr-comments");
        return null;
      }
    });
    // The draft's line is a tracked decoration, so edits above it while typing move it with its code.
    const draftMark = editor.createDecorationsCollection();
    const [drafting, setDrafting] = createSignal(false);
    const draft = (): number | null => {
      version();
      return drafting() ? (draftMark.getRange(0)?.startLineNumber ?? null) : null;
    };
    const setDraft = (line: number | null): void => {
      if (line === null) draftMark.clear();
      else
        draftMark.set([
          {
            range: new monaco.Range(line, 1, line, 1),
            options: {
              stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
            },
          },
        ]);
      setDrafting(line !== null);
    };
    const [replying, setReplying] = createSignal<ReadonlySet<number>>(new Set());
    const reply = (rootId: number, open: boolean): void => {
      setReplying((current) => {
        const next = new Set(current);
        if (open) next.add(rootId);
        else next.delete(rootId);
        return next;
      });
    };
    const cards = createMemo((): ZoneCard<Card>[] => {
      const map = anchors()?.map;
      const placed = map === undefined ? [] : threads().map((thread) => map.place(thread));
      const result: ZoneCard<Card>[] = placed.map((placement) => ({
        key: `thread:${placement.thread.rootId}`,
        afterLine: placement.afterLine,
        data: { kind: "thread", placement },
      }));
      const line = draft();
      if (line !== null)
        result.push({ key: "draft", afterLine: line, data: { kind: "draft", line } });
      return result;
    });
    createEffect(() => {
      const value = commentable();
      const connection = editorContexts.fromEditor(editor);
      if (connection?.model === model && editorContexts.get(session) === connection) {
        setContext("prCommentable", value);
      }
    });

    const submitDraft = async (body: string): Promise<string | null> => {
      const current = set();
      const line = draft();
      const mapped = anchors();
      const headLine = line === null ? null : (mapped?.map.headLine(line) ?? null);
      if (current === null || mapped === null || line === null || headLine === null) {
        return "This line isn't part of the pull request's pushed code.";
      }
      const error = await sendPrComment(session, "comment", {
        number: current.number,
        headSha: mapped.headSha,
        path,
        line: headLine,
        body,
      });
      if (error === null) setDraft(null);
      return error;
    };

    const disposeCards = createZoneCards(editor, cards, (card: Accessor<Card>) => {
      const current = card();
      if (current.kind === "draft") {
        return (
          <PrDraftCard
            line={(card() as Extract<Card, { kind: "draft" }>).line}
            viewer={set()?.viewer ?? ""}
            onSubmit={submitDraft}
            onCancel={() => setDraft(null)}
          />
        );
      }
      const placement = () => (card() as Extract<Card, { kind: "thread" }>).placement;
      const rootId = placement().thread.rootId;
      return (
        <Show when={set()}>
          {(active) => (
            <PrThreadCard
              placement={placement()}
              session={session}
              number={active().number}
              viewer={active().viewer}
              replying={replying().has(rootId)}
              onReplying={(open) => reply(rootId, open)}
            />
          )}
        </Show>
      );
    });

    const comment = (): boolean => {
      const current = set();
      const line = editor.getPosition()?.lineNumber;
      if (current === null || line === undefined || !commentable()) return false;
      const thread = cards().find((card) => card.data.kind === "thread" && card.afterLine === line);
      if (thread?.data.kind === "thread") {
        reply(thread.data.placement.thread.rootId, true);
        return true;
      }
      const map = anchors()?.map;
      if (map === undefined) {
        notify("info", "PR comment lines are still loading.", "pr-comments");
      } else if (map.headLine(line) === null) {
        notify(
          "warn",
          `Line ${line} isn't in PR #${current.number}'s pushed code — push it to comment on it.`,
          "pr-comments",
        );
      } else {
        setDraft(line);
      }
      return true;
    };

    return {
      commentable,
      comment,
      dispose: () => {
        clearTimeout(recompute);
        onContent.dispose();
        disposeCards();
        draftMark.clear();
        dispose();
      },
    };
  });
}
