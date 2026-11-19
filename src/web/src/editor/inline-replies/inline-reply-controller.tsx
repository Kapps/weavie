// Shows the replies to prompts sent from this model's selections, each a card under the line it was sent from.

import type * as monaco from "monaco-editor";
import { type Accessor, createEffect, createMemo, createRoot, onCleanup } from "solid-js";
import type { ClientSession } from "../../bridge";
import { createZoneCards, type ZoneCard } from "../zone-cards";
import { InlineReplyCard } from "./InlineReplyCard";
import { type InlineReply, inlineReplies, inlineReplyLine } from "./inline-replies-store";
import "./inline-replies.css";

export function createInlineReplies(
  session: ClientSession,
  editor: monaco.editor.ICodeEditor,
  model: monaco.editor.ITextModel,
  version: Accessor<number>,
): () => void {
  return createRoot((dispose) => {
    const replies = createMemo(() =>
      inlineReplies(session).filter((reply) => reply.model === model),
    );
    const any = createMemo(() => replies().length > 0);
    // Few editors ever hold a reply, so the card layer exists only while one does.
    createEffect(() => {
      if (!any()) return;
      const cards = createMemo((): ZoneCard<InlineReply>[] => {
        version();
        return replies().flatMap((reply) => {
          const line = inlineReplyLine(reply);
          return line === undefined ? [] : [{ key: reply.id, afterLine: line, data: reply }];
        });
      });
      onCleanup(
        createZoneCards(
          editor,
          cards,
          (reply) => <InlineReplyCard reply={reply()} session={session} />,
          () => 0,
          0,
        ),
      );
    });
    return dispose;
  });
}
