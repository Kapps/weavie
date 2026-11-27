// The reading tab's services: its copies of code, its models, where the reader is, and the stop commands.

import { createEffect, createSignal, onCleanup, onMount } from "solid-js";
import type { ClientSession, WalkthroughCode } from "../../bridge";
import { CommandIds } from "../../commands/types";
import type { FileModelScope } from "../../editor/editor-host";
import { monaco } from "../../editor/monaco-setup";
import { ownCommand, ownsCommand } from "../agent-command-owner";
import type { BlockModel } from "./walkthrough-code";
import type { PlacedQuestion } from "./walkthrough-questions";

/** The walkthrough's copies of code by hash, asked for once; a failed request is asked again next time. */
export function createSources(
  session: ClientSession,
  conversationId: string | null,
  blockId: string,
) {
  const sources = new Map<string, Promise<string>>();
  return (hash: string): Promise<string> => {
    let text = sources.get(hash);
    if (text === undefined) {
      text = session
        .feature("agent")
        .request<{ text: string }, unknown>("walkthroughSource", { conversationId, blockId, hash })
        .then((reply) => reply.text);
      text.catch(() => sources.delete(hash));
      sources.set(hash, text);
    }
    return text;
  };
}

/** A block's model: the live file while it exists; once it is gone, one shared copy per recorded text. */
export function createBlockModels(
  session: ClientSession,
  scope: FileModelScope,
  root: () => string,
) {
  const copies = new Map<string, monaco.editor.ITextModel>();
  onCleanup(() => {
    for (const model of copies.values()) model.dispose();
  });
  return async (code: WalkthroughCode, copy: string): Promise<BlockModel> => {
    const path = `${root()}/${code.path}`;
    const stat = await session
      .feature("files")
      .request<{ exists: boolean }, { path: string }>("stat", { path });
    if (stat.exists) return { model: (await scope.open(path, true)).model, deleted: false };
    const key = `${code.path}@${code.source}`;
    let model = copies.get(key);
    if (model === undefined) {
      model = monaco.editor.createModel(
        copy,
        undefined,
        monaco.Uri.from({
          scheme: "weavie-walkthrough",
          path: `/${code.path}`,
          query: code.source,
        }),
      );
      copies.set(key, model);
    }
    return { model, deleted: true };
  };
}

/**
 * The stop being read: the last whose heading has passed the top third of the tab. An observer reports crossings,
 * so scrolling reads no layout.
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
          passed.set(entry.target, entry.boundingClientRect.top <= (entry.rootBounds?.bottom ?? 0));
        }
        let at = 0;
        sections().forEach((section, index) => {
          if (passed.get(section) === true) at = index;
        });
        if (at === current()) return;
        setCurrent(at);
        onChange(at);
      },
      { root: scroller(), rootMargin: "0px 0px -66% 0px" },
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

/** Gives the stop commands to the walkthrough the user last pressed or focused. */
export function ownStopCommands(
  root: () => HTMLElement,
  go: (index: number) => void,
  current: () => number,
): void {
  let releases: (() => void)[] = [];
  const own = (event: Event): void => {
    if (ownsCommand(CommandIds.nextWalkthroughStop, releases[0] ?? null)) return;
    const element = event.currentTarget as Element;
    const step = (delta: number) => (): boolean => {
      go(current() + delta);
      return true;
    };
    releases = [
      ownCommand(CommandIds.nextWalkthroughStop, "walkthroughFocused", element, step(1), false),
      ownCommand(
        CommandIds.previousWalkthroughStop,
        "walkthroughFocused",
        element,
        step(-1),
        false,
      ),
    ];
  };
  onMount(() => {
    const element = root();
    for (const type of ["focusin", "pointerdown"]) element.addEventListener(type, own);
    onCleanup(() => {
      for (const type of ["focusin", "pointerdown"]) element.removeEventListener(type, own);
      for (const release of releases) release();
    });
  });
}

/** Keeps the text each question was asked about lit in the prose while its card is drawn. */
export function highlightQuotes(root: () => HTMLElement, placed: () => readonly PlacedQuestion[]) {
  createEffect(() => {
    const ranges: Range[] = [];
    for (const { question, place, moved } of placed()) {
      const host = moved ? null : root().querySelector(`[data-place="${CSS.escape(place)}"]`);
      const range = host == null ? null : findText(host, question.anchor.quote);
      if (range !== null) ranges.push(range);
    }
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
