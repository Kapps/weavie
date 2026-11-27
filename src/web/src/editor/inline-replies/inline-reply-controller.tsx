// Shows the replies to prompts sent from selections in this model's file, each a card under the code it was asked
// about.

import type * as monaco from "monaco-editor";
import { type Accessor, createEffect, createMemo, createRoot, onCleanup } from "solid-js";
import type { ClientSession } from "../../bridge";
import { createZoneCards, type ZoneCard } from "../zone-cards";
import { InlineReplyCard } from "./InlineReplyCard";
import {
  type InlineReply,
  inlineReplies,
  placeReply,
  type ReplyPlace,
  replyIn,
} from "./inline-replies-store";
import "./inline-replies.css";

export function createInlineReplies(
  session: ClientSession,
  editor: monaco.editor.ICodeEditor,
  model: monaco.editor.ITextModel,
  version: Accessor<number>,
): () => void {
  return createRoot((dispose) => {
    const replies = createMemo(() =>
      inlineReplies(session).filter((reply) => replyIn(reply, model)),
    );
    const any = createMemo(() => replies().length > 0);
    // Few editors ever hold a reply, so the card layer exists only while one does.
    createEffect(() => {
      if (!any()) return;
      const cards = createMemo((): ZoneCard<{ reply: InlineReply; place: ReplyPlace }>[] => {
        version();
        return replies().flatMap((reply) => {
          const place = placeReply(session, reply, model);
          return place === null
            ? []
            : [{ key: reply.id, afterLine: place.line, data: { reply, place } }];
        });
      });
      onCleanup(
        createZoneCards(
          editor,
          cards,
          (card) => (
            <InlineReplyCard reply={card().reply} moved={card().place.moved} session={session} />
          ),
          0,
          false,
        ),
      );
    });
    return dispose;
  });
}
