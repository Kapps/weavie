import { File, ListFilter, X } from "lucide-solid";
import { createEffect, createSignal, createUniqueId, For, type JSX, on, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { createFileSearch } from "../chrome/create-file-search";
import { highlightSlice } from "../chrome/highlight";
import { recentFiles } from "../chrome/recent-files-store";
import { liveKeyHint } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { samePath } from "../editor/fs-path";
import { createListNavigation } from "../list-navigation";

export function BrowserFilter(props: {
  root: string;
  session: ClientSession | null;
  files: string[];
  pending: boolean;
  request: { session: ClientSession } | null;
  onRequestHandled: () => void;
  currentFile: string | null;
  onOpen: (path: string) => void;
  children: JSX.Element;
}): JSX.Element {
  let button!: HTMLButtonElement;
  let input: HTMLInputElement | undefined;
  const listId = createUniqueId();
  const [filtering, setFiltering] = createSignal(false);
  const [query, setQuery] = createSignal("");
  const search = createFileSearch({
    files: () => props.files,
    root: () => props.root,
    query,
    recent: recentFiles,
    currentFile: () => props.currentFile,
  });
  const needle = search.query;
  const matches = search.view;
  const dismiss = (): void => {
    setQuery("");
    setFiltering(false);
    button.focus();
  };
  const navigation = createListNavigation({
    count: () => matches().length,
    edges: "wrap",
    initialIndex: 0,
    acceptKeys: ["Enter"],
    onAccept: (index) => {
      const match = matches()[index];
      if (match !== undefined) props.onOpen(match.row.abs);
    },
    onDismiss: dismiss,
  });
  createEffect(on(query, () => navigation.setIndex(0)));
  createEffect(
    on(
      () => props.session,
      () => {
        setQuery("");
        setFiltering(false);
      },
    ),
  );
  createEffect(
    on(
      () => props.request,
      (request) => {
        if (request === null || request.session !== props.session) return;
        setFiltering(true);
        props.onRequestHandled();
        queueMicrotask(() => {
          if (request.session !== props.session || !input?.isConnected) return;
          input.focus();
          input.select();
        });
      },
    ),
  );

  return (
    <>
      <div class="browser-toolbar">
        <button
          ref={button}
          type="button"
          class="browser-filter-button"
          aria-expanded={filtering()}
          title={`Filter files${liveKeyHint(CommandIds.filterFileBrowser)}`}
          onClick={() => void runCommandWithFeedback(CommandIds.filterFileBrowser)}
        >
          <ListFilter size={14} />
          Filter
        </button>
        <Show when={needle().length > 0}>
          <span class="browser-filter-count" role="status">
            {props.pending ? "Loading files…" : `${search.total()} matches`}
          </span>
        </Show>
      </div>
      <Show when={filtering()}>
        <div class="browser-filter-box">
          <input
            ref={input}
            class="browser-filter-input"
            aria-label="Filter files by name or path"
            role="combobox"
            aria-autocomplete="list"
            aria-expanded={needle().length > 0}
            aria-controls={listId}
            aria-activedescendant={
              navigation.index() >= 0 ? `${listId}-${navigation.index()}` : undefined
            }
            placeholder="Filter by name or path…"
            value={query()}
            onInput={(event) => setQuery(event.currentTarget.value)}
            onKeyDown={navigation.onKeyDown}
          />
          <button type="button" title="Clear filter (Esc)" onClick={dismiss}>
            <X size={14} />
          </button>
        </div>
      </Show>
      <Show when={search.hiddenCount() > 0}>
        <div class="browser-empty browser-filter-more" role="status">
          +{search.hiddenCount()} more — type to filter
        </div>
      </Show>
      <div class="browser-body">
        <div hidden={needle().length > 0}>{props.children}</div>
        <Show when={needle().length > 0}>
          <Show
            when={matches().length > 0}
            fallback={
              <div class="browser-empty">
                {props.pending ? "Loading files…" : "No matching files"}
              </div>
            }
          >
            <div id={listId} role="listbox" aria-label="Matching files">
              <For each={matches()}>
                {(match, index) => (
                  <button
                    {...navigation.row(index())}
                    type="button"
                    role="option"
                    id={`${listId}-${index()}`}
                    aria-selected={index() === navigation.index()}
                    class="browser-row browser-filter-result file-tree-row"
                    classList={{
                      selected: index() === navigation.index(),
                      active:
                        props.currentFile !== null && samePath(props.currentFile, match.row.abs),
                    }}
                    title={match.row.rel}
                    onClick={() => props.onOpen(match.row.abs)}
                  >
                    <span class="browser-icon file-tree-icon">
                      <File />
                    </span>
                    <span class="browser-filter-path">
                      <span class="browser-name file-tree-name">
                        {highlightSlice(match.row.leaf, match.positions, match.row.leafStart)}
                      </span>
                      <Show when={match.row.dir.length > 0}>
                        <span class="browser-filter-dir">
                          {highlightSlice(match.row.dir, match.positions, 0)}
                        </span>
                      </Show>
                    </span>
                  </button>
                )}
              </For>
            </div>
          </Show>
        </Show>
      </div>
    </>
  );
}
