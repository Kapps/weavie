// The reading tab's services: its copies of code, its models, where the reader is, and the stop commands.

import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import type { ClientSession, WalkthroughCode } from "../../bridge";
import type { FileModelScope } from "../../editor/editor-host";
import { createRequestCache } from "../../messaging/request-cache";
import { agentPaneModel } from "../pane-store";
import type { BlockModel } from "./walkthrough-code";
import type { PlacedQuestion } from "./walkthrough-questions";

/** The walkthrough's copies of code by hash, asked for once; a failed request is asked again next time. */
export function createSources(
  session: ClientSession,
  conversationId: string | null,
  blockId: string,
) {
  const sources = createRequestCache<string>(() => true);
  return (hash: string): Promise<string> =>
    sources.get(hash, () =>
      session
        .feature("agent")
        .request<{ text: string }, unknown>("walkthroughSource", { conversationId, blockId, hash })
        .then(
          (reply) => reply.text,
          () => {
            throw new Error("This code is no longer stored");
          },
        ),
    );
}

/** The questions asked from walkthrough `blockId` of conversation `conversationId`, tracked reactively. */
export const walkthroughQuestions = (
  session: ClientSession,
  conversationId: string | null,
  blockId: string,
) =>
  (agentPaneModel(session)?.questions() ?? []).filter(
    ({ anchor }) =>
      anchor.blockId === blockId && (anchor.conversationId ?? null) === conversationId,
  );

/** A block's model: the live file while it exists; once it is gone, a copy of the text it recorded. */
export function createBlockModels(
  session: ClientSession,
  scope: FileModelScope,
  root: () => string,
) {
  return async (code: WalkthroughCode, copy: string): Promise<BlockModel> => {
    const path = `${root()}/${code.path}`;
    const { exists } = await session
      .feature("files")
      .request<{ exists: boolean }, { path: string }>("stat", { path });
    return { model: (await scope.open(path, exists ? null : copy)).model, deleted: !exists };
  };
}

/**
 * The stop being read: the last whose heading has passed the top third of the tab. The observer's band runs from far
 * above the tab down to that line, so a stop is in it exactly when it has passed, and a jump that skips the line
 * still changes it. Scrolling reads no layout.
 */
export function trackCurrentStop(
  scroller: () => HTMLElement,
  sections: () => HTMLElement[],
  count: () => number,
  onChange: (index: number) => void,
) {
  const [current, setCurrent] = createSignal(0);
  const passed = new Map<Element, boolean>();
  let observer: IntersectionObserver | undefined;
  onMount(() => {
    observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          passed.set(entry.target, entry.isIntersecting);
        }
        let at = 0;
        sections().forEach((section, index) => {
          if (passed.get(section) === true) at = index;
        });
        if (at === current()) return;
        setCurrent(at);
        onChange(at);
      },
      { root: scroller(), rootMargin: "1000000px 0px -66% 0px" },
    );
    onCleanup(() => observer?.disconnect());
  });
  // Stops an update adds are observed too.
  createEffect(() => {
    count();
    queueMicrotask(() => {
      for (const section of sections()) observer?.observe(section);
    });
  });
  return current;
}

/** The context held while a walkthrough owns the stop commands; its target steps that walkthrough's stops. */
export const walkthroughFocusContext = "walkthroughFocused";

/** Keeps the text each question was asked about lit in the prose while its card is drawn. */
export function highlightQuotes(root: () => HTMLElement, placed: () => readonly PlacedQuestion[]) {
  createEffect(() => {
    const ranges: Range[] = [];
    for (const { question, place, moved } of placed()) {
      const host = moved ? null : root().querySelector(`[data-place="${CSS.escape(place)}"]`);
      const range = host == null ? null : findText(host, question.anchor.quote);
      if (range !== null) ranges.push(range);
    }
    // A cosmetic mark: an engine without the Highlight API (WebKit before 17.2) shows the card without it.
    if (typeof Highlight === "undefined") return;
    CSS.highlights.set("walkthrough-asked", new Highlight(...ranges));
    onCleanup(() => CSS.highlights.delete("walkthrough-asked"));
  });
}

function findText(host: Element, text: string): Range | null {
  const walker = document.createTreeWalker(host, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const at = node.textContent?.indexOf(text) ?? -1;
    if (at < 0) continue;
    const range = document.createRange();
    range.setStart(node, at);
    range.setEnd(node, at + text.length);
    return range;
  }
  return null;
}
