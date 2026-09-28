import { dispose } from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import { createEffect, createRoot, createSignal, untrack } from "solid-js";
import type { ReviewCommentInfo } from "../../bridge";
import { samePath } from "../fs-path";
import type { monaco } from "../monaco-setup";
import { createReviewCommentComposer } from "./review-comment-composer";
import type { ReviewCommentDraft } from "./review-comment-drafts";
import type { ReviewCommentContext } from "./review-comment-session";
import { ReviewZoneOrder } from "./review-zone-order";

interface CommentZone {
  zone: monaco.editor.IViewZone & { heightInPx: number };
  placement: { layout(): void; dispose(): void };
  messages: HTMLElement;
  signature: string;
  composer: ReturnType<typeof createReviewCommentComposer>;
  dispose(): void;
}

export interface ReviewCommentPlacement {
  model(): monaco.editor.ITextModel | null;
  place(zone: monaco.editor.IViewZone & { heightInPx: number }): {
    layout(): void;
    dispose(): void;
  };
}

/** Ordinary inline review places the shared thread presenter in Monaco-owned zones. */
export function createReviewCommentView(
  editor: monaco.editor.IStandaloneCodeEditor,
  updateGeometry: (change: () => void) => void,
) {
  const changeZones = (change: Parameters<typeof editor.changeViewZones>[0]): void =>
    updateGeometry(() => editor.changeViewZones(change));
  return createReviewCommentPresenter({
    model: () => editor.getModel(),
    place: (zone) => {
      let id = "";
      changeZones((accessor) => {
        id = accessor.addZone(zone);
      });
      return {
        layout: () => changeZones((accessor) => accessor.layoutZone(id)),
        dispose: () => changeZones((accessor) => accessor.removeZone(id)),
      };
    },
  });
}

/** Thread identity, textarea selection, and composition survive presentation replacement. */
export function createReviewCommentPresenter(placement: ReviewCommentPlacement) {
  const [current, setCurrent] = createSignal<ReviewCommentContext>();
  const zones = new Map<number | ReviewCommentDraft, CommentZone>();
  const [zoneRevision, setZoneRevision] = createSignal(0);
  const changedZones = (): void => {
    setZoneRevision((value) => value + 1);
  };
  let previous: ReviewCommentContext | undefined;
  let disposed = false;
  const clear = (): void => {
    const previous = [...zones.values()];
    zones.clear();
    if (previous.length > 0) changedZones();
    dispose(previous);
  };
  const createZone = (
    context: ReviewCommentContext,
    draft: ReviewCommentDraft,
    line: number,
  ): CommentZone => {
    const content = document.createElement("div");
    content.className = "weavie-pr-thread";
    if (draft.target.kind === "new") content.classList.add("weavie-pr-thread-new");
    const messages = document.createElement("div");
    const composer = createReviewCommentComposer(context.owner.drafts, draft);
    content.append(messages, composer.element);
    const domNode = document.createElement("div");
    domNode.append(content);
    const zone = {
      afterLineNumber: line,
      showInHiddenAreas: true,
      heightInPx: 0,
      ordinal: draft.target.kind === "new" ? ReviewZoneOrder.draft : ReviewZoneOrder.thread,
      domNode,
    };
    const placed = placement.place(zone);
    const observer = new ResizeObserver(() => {
      const style = getComputedStyle(content);
      const height =
        content.getBoundingClientRect().height +
        parseFloat(style.marginTop) +
        parseFloat(style.marginBottom);
      if (height > 0 && height !== zone.heightInPx) {
        zone.heightInPx = height;
        placed.layout();
      }
    });
    observer.observe(content);
    return {
      zone,
      placement: placed,
      composer,
      messages,
      signature: "",
      dispose: () => {
        observer.disconnect();
        composer.dispose();
        placed.dispose();
      },
    };
  };
  const reconcile = (
    context: ReviewCommentContext | undefined,
    drafts: readonly ReviewCommentDraft[],
  ): void => {
    if (disposed) return;
    if (
      !context ||
      previous?.owner !== context.owner ||
      previous.file.number !== context.file.number ||
      !samePath(previous.file.path, context.file.path)
    )
      clear();
    previous = context;
    const model = placement.model();
    if (!context || !model) return;
    const groups = new Map<number, ReviewCommentInfo[]>();
    for (const comment of context.comments) {
      const rootId = comment.inReplyTo === 0 ? comment.id : comment.inReplyTo;
      const group = groups.get(rootId) ?? [];
      group.push(comment);
      groups.set(rootId, group);
    }
    const wanted = new Set<number | ReviewCommentDraft>();
    const place = (
      key: number | ReviewCommentDraft,
      draft: ReviewCommentDraft,
      line: number,
    ): CommentZone => {
      wanted.add(key);
      const clamped = Math.max(1, Math.min(model.getLineCount(), line));
      let held = zones.get(key);
      if (!held) {
        held = createZone(context, draft, clamped);
        zones.set(key, held);
        changedZones();
      } else if (held.zone.afterLineNumber !== clamped) {
        held.zone.afterLineNumber = clamped;
        held.placement.layout();
      }
      return held;
    };
    for (const [rootId, comments] of groups) {
      const root = comments.find((comment) => comment.id === rootId);
      const draft = context.owner.openReply(context, rootId);
      const held = place(rootId, draft, (root ?? comments[0]!).line);
      const signature = JSON.stringify(comments);
      if (held.signature !== signature) {
        held.signature = signature;
        held.messages.replaceChildren(
          ...comments.map((comment) => {
            const item = document.createElement("div");
            item.className = "weavie-pr-comment";
            const author = document.createElement("span");
            author.className = "weavie-pr-comment-author";
            author.textContent = `@${comment.author}`;
            const body = document.createElement("span");
            body.className = "weavie-pr-comment-body";
            body.textContent = comment.body;
            item.append(author, body);
            return item;
          }),
        );
      }
    }
    for (const draft of drafts) {
      if (
        draft.file.number === context.file.number &&
        samePath(draft.file.path, context.file.path) &&
        draft.target.kind === "new"
      )
        place(draft, draft, draft.target.anchor.line);
    }
    for (const [key, held] of zones) {
      if (!wanted.has(key)) {
        zones.delete(key);
        changedZones();
        held.dispose();
      }
    }
  };
  const disposeEffect = createRoot((dispose) => {
    createEffect(() => {
      const context = current();
      placement.model();
      const drafts = context?.owner.drafts.retained() ?? [];
      untrack(() => reconcile(context, drafts));
    });
    return dispose;
  });
  return {
    configure: (context: ReviewCommentContext | undefined) => setCurrent(context),
    open: (line: number): void => {
      const context = current();
      if (!context) throw new Error("This file has no pull-request comment owner.");
      const model = placement.model();
      if (!model) throw new Error("This file is no longer open.");
      const draft = context.owner.openNew(context, model, line);
      reconcile(context, context.owner.drafts.retained());
      zones.get(draft)!.composer.input.focus();
    },
    focused: (): boolean => {
      zoneRevision();
      return [...zones.values()].some((zone) => zone.composer.focused());
    },
    retained: (): boolean => {
      const context = current();
      if (!context) return false;
      return context.owner.drafts
        .retained()
        .some(
          (draft) =>
            draft.file.number === context.file.number &&
            samePath(draft.file.path, context.file.path),
        );
    },
    dispose: (): void => {
      disposed = true;
      disposeEffect();
      clear();
    },
  };
}
