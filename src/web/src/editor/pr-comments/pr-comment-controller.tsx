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
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { describeError } from "../../lsp/lsp-errors";
import { notify } from "../../notify/notify";
import { editorContexts } from "../editor-context";
import type { FixItem } from "../FixPrompt";
import { samePath } from "../fs-path";
import { monaco } from "../monaco-setup";
import { DIFF_RECOMPUTE_DEBOUNCE_MS } from "../review/diff-zones";
import { reviewItems } from "../review-items/review-items";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "../session-uri";
import { createZoneCards, type ZoneCard } from "../zone-cards";
import { type AnchorMap, createAnchorMap, type ThreadPlacement, threadFixItem } from "./anchor-map";
import { prefillComposer } from "./PrCommentComposer";
import { PrDraftCard, PrThreadCard, replyDraftKey } from "./PrThreadCard";
import {
  type PrCommentMode,
  type PrSources,
  prCommentsFor,
  prCommentsVisible,
  sendPrComment,
} from "./pr-comments-store";
import "./pr-comments.css";

export interface PrCommentController {
  /** Whether this file is part of the branch's pull request, so a new comment can be left on it. */
  commentable(): boolean;
  /** Opens a comment box on a line, holding `initial`: the reply of the thread anchored there, else a new comment. */
  comment(line: number, initial: string): boolean;
  /** The thread anchored on a buffer line, as a Fix item. */
  fixItemAt(line: number): FixItem | null;
  dispose(): void;
}

type Card =
  | { kind: "thread"; placement: ThreadPlacement }
  | { kind: "draft"; line: number; key: string };

const inert: PrCommentController = {
  commentable: () => false,
  comment: () => false,
  fixItemAt: () => null,
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
    const threads = createMemo(
      () => set()?.threads.filter((thread) => samePath(thread.path, path)) ?? [],
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
    // The open draft's identity is fixed by the line it opened on, so reopening elsewhere starts a new box.
    const [draftKey, setDraftKey] = createSignal<string | null>(null);
    const draft = (): number | null => {
      version();
      return draftKey() !== null ? (draftMark.getRange(0)?.startLineNumber ?? null) : null;
    };
    const setDraft = (line: number | null, key: string | null): void => {
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
      setDraftKey(key);
    };
    const [replying, setReplying] = createSignal<ReadonlySet<string>>(new Set());
    // Outdated and resolved threads start collapsed; the user's own collapse/expand wins from then on.
    const [collapsedByUser, setCollapsedByUser] = createSignal<ReadonlyMap<string, boolean>>(
      new Map(),
    );
    const collapsed = (placement: ThreadPlacement): boolean =>
      collapsedByUser().get(placement.thread.id) ??
      (placement.note === "outdated" || placement.thread.resolved);
    const collapse = (id: string, value: boolean): void => {
      setCollapsedByUser((current) => new Map(current).set(id, value));
    };
    const reply = (id: string, open: boolean): void => {
      if (open) collapse(id, false);
      setReplying((current) => {
        const next = new Set(current);
        if (open) next.add(id);
        else next.delete(id);
        return next;
      });
    };
    // Every thread's place, shown or not: commenting while threads are hidden still replies to the line's thread.
    const placements = createMemo((): ThreadPlacement[] => {
      const map = anchors()?.map;
      return map === undefined ? [] : threads().map((thread) => map.place(thread));
    });
    const cards = createMemo((): ZoneCard<Card>[] => {
      const result: ZoneCard<Card>[] = (prCommentsVisible() ? placements() : []).map(
        (placement) => ({
          key: `thread:${placement.thread.id}`,
          afterLine: placement.afterLine,
          data: { kind: "thread", placement },
        }),
      );
      const line = draft();
      const key = draftKey();
      if (line !== null && key !== null)
        result.push({ key, afterLine: line, data: { kind: "draft", line, key } });
      return result;
    });
    createEffect(() => {
      const value = commentable();
      const connection = editorContexts.fromEditor(editor);
      if (connection?.model === model && editorContexts.get(session) === connection) {
        setContext("prCommentable", value);
      }
    });

    const submitDraft = async (mode: PrCommentMode, body: string): Promise<string | null> => {
      const current = set();
      const line = draft();
      const key = draftKey();
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
        mode,
      });
      // The post's reply can land after the user opened a draft elsewhere, which it must not close.
      if (error === null && draftKey() === key) setDraft(null, null);
      return error;
    };

    // The applied review's floating toolbar covers the bottom of the editor it reviews.
    const reviewToolbarInset = (): number => {
      const editorBox = editor.getDomNode()?.getBoundingClientRect();
      const toolbar = editor
        .getContainerDomNode()
        .closest("[data-kind]")
        ?.querySelector(".weavie-inline-toolbar")
        ?.getBoundingClientRect();
      return editorBox === undefined || toolbar == null || toolbar.top >= editorBox.bottom
        ? 0
        : editorBox.bottom - toolbar.top;
    };

    const disposeCards = createZoneCards(
      editor,
      cards,
      (card: Accessor<Card>) => {
        const current = card();
        if (current.kind === "draft") {
          return (
            <Show when={set()}>
              {(active) => (
                <PrDraftCard
                  draftKey={current.key}
                  line={(card() as Extract<Card, { kind: "draft" }>).line}
                  set={active()}
                  onSubmit={submitDraft}
                  onCancel={() => setDraft(null, null)}
                />
              )}
            </Show>
          );
        }
        const placement = () => (card() as Extract<Card, { kind: "thread" }>).placement;
        const id = placement().thread.id;
        return (
          <Show when={set()}>
            {(active) => (
              <PrThreadCard
                placement={placement()}
                session={session}
                set={active()}
                collapsed={collapsed(placement())}
                current={reviewItems.isCurrentThread(session, id)}
                onCollapsed={(value) => collapse(id, value)}
                replying={replying().has(id)}
                onReplying={(open) => reply(id, open)}
                fix={() => fixItem(placement())}
              />
            )}
          </Show>
        );
      },
      reviewToolbarInset,
    );

    const fixItem = (placement: ThreadPlacement): FixItem | null =>
      threadFixItem(placement, path, (line) => model.getLineContent(line));

    const comment = (line: number, initial: string): boolean => {
      const current = set();
      if (current === null || !commentable()) return false;
      // Commenting while threads are hidden would post into nothing visible.
      if (!prCommentsVisible())
        void runCommandWithFeedback(CommandIds.prToggleComments, { show: true });
      const thread = placements().find((placement) => placement.afterLine === line);
      if (thread !== undefined) {
        const id = thread.thread.id;
        prefillComposer(replyDraftKey(current.number, id), initial);
        reply(id, true);
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
        const key = `new:${current.number}:${path}:${line}`;
        prefillComposer(key, initial);
        setDraft(line, key);
      }
      return true;
    };

    // A step outlines a thread with no editor; the first displayed binding showing it moves the cursor there.
    createEffect(() => {
      const id = reviewItems.unclaimedThread(session);
      const card = cards().find(
        (entry) => entry.data.kind === "thread" && entry.data.placement.thread.id === id,
      );
      if (
        card === undefined ||
        id === null ||
        editor.getContainerDomNode().getClientRects().length === 0
      )
        return;
      const line = Math.max(1, card.afterLine);
      reviewItems.claimThread(session, line);
      collapse(id, false);
      editor.setPosition({ lineNumber: line, column: 1 });
      editor.revealLineInCenterIfOutsideViewport(line);
      editor.focus();
    });
    // Cards sit beside Monaco, so this is a click in the code: the next step starts from the cursor.
    const onMouseDown = editor.onMouseDown(() => reviewItems.clearThread(session));

    return {
      commentable,
      comment,
      fixItemAt: (line) =>
        cards()
          .filter((card) => Math.max(1, card.afterLine) === line)
          .map((card) => (card.data.kind === "thread" ? fixItem(card.data.placement) : null))
          .find((item) => item !== null) ?? null,
      dispose: () => {
        clearTimeout(recompute);
        onContent.dispose();
        onMouseDown.dispose();
        disposeCards();
        draftMark.clear();
        dispose();
      },
    };
  });
}
