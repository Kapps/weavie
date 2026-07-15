import { ChevronDown, ChevronRight, Search, X } from "lucide-solid";
import { createEffect, For, type JSX, on, onCleanup, Show } from "solid-js";
import type { SearchMatch } from "../bridge";
import { setContext } from "../commands/context";
import { keyLabel } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import { highlightSlice } from "./highlight";
import { SearchToggles } from "./SearchToggles";
import { matchPositions } from "./search-model";
import {
  cancelPreview,
  commitCurrentTerm,
  cycleHistory,
  moveAndPreview,
  openSelected,
  searchState as s,
  selectMatch,
  setGlobs,
  setQuery,
  toggleGroup,
  toggleSearchOption,
} from "./search-store";

function leafName(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] ?? path) : path;
}

/**
 * Project-wide content search (find in files): a left-docked panel over the module search store. The query
 * seeds from the editor selection; match case / whole word / regex toggle inline (their chords advertised via
 * the catalog); include/exclude globs filter paths. Arrows live-preview the selected match without leaving the
 * input, Enter commits (opens + focuses the editor), Esc closes. The host greps the active session's worktree.
 */
export function SearchPanel(props: { onClose: () => void }): JSX.Element {
  let root!: HTMLDivElement;
  let input!: HTMLInputElement;
  let listRef: HTMLDivElement | undefined;

  // Focus + select the input on mount and on every re-seed (Ctrl+Shift+F while already open).
  createEffect(
    on(s.seedNonce, () => {
      input.focus();
      input.select();
    }),
  );

  // Keep the selected row in view — for arrows and F4 stepping alike.
  createEffect(
    on(s.selected, () => {
      queueMicrotask(() => {
        listRef?.querySelector('[data-selected="true"]')?.scrollIntoView({ block: "nearest" });
      });
    }),
  );

  onCleanup(() => {
    setContext("searchPanelFocused", false);
    cancelPreview();
    // A search that ran but was never opened still counts as recent — record it as the panel closes.
    commitCurrentTerm();
  });

  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "Escape") {
      e.preventDefault();
      props.onClose();
      return;
    }
    // Arrows/Enter drive the result list only from the query input — in a glob field they're plain text
    // editing (Enter is a natural "apply", not "open the selected match").
    if ((e.target as HTMLElement).classList.contains("search-glob")) {
      return;
    }

    if (e.altKey && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
      // Alt+Up/Down cycle recent search terms (Up = older), leaving the plain arrows for result navigation.
      e.preventDefault();
      cycleHistory(e.key === "ArrowUp" ? 1 : -1);
    } else if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      moveAndPreview(e.key === "ArrowDown" ? 1 : -1);
    } else if (e.key === "Enter") {
      e.preventDefault();
      openSelected();
    }
  };

  const toggle = (key: "caseSensitive" | "wholeWord" | "regex" | "excludeGitignored"): void => {
    toggleSearchOption(key);
    input.focus();
  };

  const summary = (): string => {
    const count = s.matches().length;
    const files = s.groups().length;
    return s.truncated()
      ? `Showing the first ${count} matches — narrow the search`
      : `${count} ${count === 1 ? "match" : "matches"} in ${files} ${files === 1 ? "file" : "files"}`;
  };

  const highlighted = (match: SearchMatch): JSX.Element => {
    const text = match.preview.trim();
    const a = s.applied();
    return highlightSlice(text, matchPositions(text, a.query, a.options), 0);
  };

  return (
    <div
      ref={root}
      class="search-panel"
      role="search"
      onKeyDown={onKeyDown}
      onFocusIn={() => setContext("searchPanelFocused", true)}
      onFocusOut={(e) =>
        setContext("searchPanelFocused", root.contains(e.relatedTarget as Node | null))
      }
    >
      <div class="search-head">
        <span class="search-title">Search</span>
        <button
          type="button"
          class="search-icon-btn"
          title="Close (Esc)"
          onClick={() => props.onClose()}
        >
          <X />
        </button>
      </div>
      <div class="search-input-row">
        <span class="search-input-icon" aria-hidden="true">
          <Search />
        </span>
        <input
          ref={input}
          class="search-input"
          type="text"
          spellcheck={false}
          placeholder="Search in files"
          value={s.query()}
          onInput={(e) => setQuery(e.currentTarget.value)}
        />
        <SearchToggles options={s.options()} onToggle={toggle} />
      </div>
      <div class="search-filters">
        <input
          class="search-glob"
          type="text"
          spellcheck={false}
          placeholder="Files to include (e.g. src/, *.ts)"
          value={s.options().include}
          onInput={(e) => setGlobs("include", e.currentTarget.value)}
        />
        <input
          class="search-glob"
          type="text"
          spellcheck={false}
          placeholder="Files to exclude (e.g. *.test.ts, dist/)"
          value={s.options().exclude}
          onInput={(e) => setGlobs("exclude", e.currentTarget.value)}
        />
      </div>
      <Show when={s.error() !== null}>
        <div class="search-error">Search failed: {s.error()}</div>
      </Show>
      <Show when={s.settled() && s.error() === null && s.matches().length > 0}>
        <div class="search-summary" classList={{ warn: s.truncated() }}>
          {summary()}
        </div>
      </Show>
      <div class="search-body" ref={listRef}>
        <Show
          when={s.matches().length > 0}
          fallback={
            <Show when={s.query().trim().length > 0 && s.settled() && s.error() === null}>
              <div class="search-empty">
                No results
                {s.options().include.length > 0 || s.options().exclude.length > 0
                  ? " — check the include/exclude filters"
                  : ""}
              </div>
            </Show>
          }
        >
          <For each={s.groups()}>
            {(group) => (
              <div class="search-group">
                <button
                  type="button"
                  class="search-group-head"
                  tabindex={-1}
                  title={group.path}
                  onClick={() => toggleGroup(group.path)}
                >
                  <span class="search-twisty" aria-hidden="true">
                    <Show when={s.collapsed().has(group.path)} fallback={<ChevronDown />}>
                      <ChevronRight />
                    </Show>
                  </span>
                  <span class="search-group-name">{leafName(group.path)}</span>
                  <span class="search-group-count">{group.matches.length}</span>
                </button>
                <Show when={!s.collapsed().has(group.path)}>
                  <For each={group.matches}>
                    {(match) => {
                      const index = (): number => s.matches().indexOf(match);
                      return (
                        <button
                          type="button"
                          class="search-row"
                          tabindex={-1}
                          data-selected={index() === s.selected()}
                          classList={{ selected: index() === s.selected() }}
                          onMouseDown={(e) => {
                            e.preventDefault();
                            selectMatch(index());
                            openSelected();
                          }}
                        >
                          <span class="search-row-line">{match.line}</span>
                          <span class="search-row-preview">{highlighted(match)}</span>
                        </button>
                      );
                    }}
                  </For>
                </Show>
              </div>
            )}
          </For>
        </Show>
      </div>
      <div class="search-hints">
        <span>
          <kbd>↑↓</kbd> preview
        </span>
        <span>
          <kbd>Alt+↑↓</kbd> history
        </span>
        <span>
          <kbd>Enter</kbd> open
        </span>
        <span>
          <kbd>{keyLabel(CommandIds.searchNextResult)}</kbd> from editor
        </span>
      </div>
    </div>
  );
}
