import { ChevronDown, ChevronRight, Search, X } from "lucide-solid";
import { createMemo, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { onHostMessage, postToHost, type SearchMatch } from "../bridge";

// Debounce so each keystroke doesn't spawn a git grep; ~200ms is responsive without thrashing.
const DEBOUNCE_MS = 200;

function leafName(path: string): string {
  const parts = path.split(/[\\/]/).filter((p) => p.length > 0);
  return parts.length > 0 ? (parts[parts.length - 1] ?? path) : path;
}

// Matches grouped by their file path, preserving git grep's order (files in first-seen order, lines within).
interface FileGroup {
  path: string;
  matches: SearchMatch[];
}

function groupByFile(matches: SearchMatch[]): FileGroup[] {
  const groups: FileGroup[] = [];
  const byPath = new Map<string, FileGroup>();
  for (const match of matches) {
    let group = byPath.get(match.path);
    if (group === undefined) {
      group = { path: match.path, matches: [] };
      byPath.set(match.path, group);
      groups.push(group);
    }
    group.matches.push(match);
  }
  return groups;
}

/**
 * Project-wide content search (find in files): a left-docked panel with a debounced query input and the
 * matches grouped by file (line + preview per row). Click or Enter on a row jumps to that file:line via the
 * host's reveal-file; arrows move the selection, Esc closes. The host runs `git grep` over the active
 * session's worktree and replies with find-in-files-results (echoing the query, so a stale reply is dropped).
 */
export function SearchPanel(props: { onClose: () => void }): JSX.Element {
  const [query, setQuery] = createSignal("");
  const [matches, setMatches] = createSignal<SearchMatch[]>([]);
  const [truncated, setTruncated] = createSignal(false);
  // The git-search error (e.g. git unavailable), so a failed search isn't reported as "No results".
  const [error, setError] = createSignal<string | null>(null);
  // Whether a reply for the current query has arrived, so "No results" only shows once the search settled.
  const [settled, setSettled] = createSignal(true);
  // The selected row in the flattened match list (index into matches()); -1 when there are none.
  const [selected, setSelected] = createSignal(0);
  const [collapsed, setCollapsed] = createSignal<Set<string>>(new Set());
  let input!: HTMLInputElement;
  let listRef: HTMLDivElement | undefined;
  let debounceTimer = 0;

  const groups = createMemo<FileGroup[]>(() => groupByFile(matches()));

  const search = (q: string): void => {
    const trimmed = q.trim();
    if (trimmed.length === 0) {
      setMatches([]);
      setTruncated(false);
      setError(null);
      setSettled(true);
      return;
    }
    setSettled(false);
    postToHost({ type: "find-in-files", query: trimmed });
  };

  const onInput = (value: string): void => {
    setQuery(value);
    window.clearTimeout(debounceTimer);
    debounceTimer = window.setTimeout(() => search(value), DEBOUNCE_MS);
  };

  const openMatch = (match: SearchMatch | undefined): void => {
    if (match === undefined) {
      return;
    }
    // Preview tab (single-jump), matching the omnibar/file-browser open; the editor reuses an open tab.
    postToHost({ type: "reveal-file", path: match.path, line: match.line, preview: true });
  };

  const scrollSelectedIntoView = (): void => {
    queueMicrotask(() => {
      listRef?.querySelector('[data-selected="true"]')?.scrollIntoView({ block: "nearest" });
    });
  };

  // Indices into matches() that are actually rendered (their file group is expanded). Keyboard nav moves
  // over these, so the selection can never wander into a collapsed group and vanish off screen.
  const visibleIndices = createMemo<number[]>(() => {
    const hidden = collapsed();
    const indices: number[] = [];
    matches().forEach((match, i) => {
      if (!hidden.has(match.path)) {
        indices.push(i);
      }
    });
    return indices;
  });

  const move = (delta: number): void => {
    const visible = visibleIndices();
    if (visible.length === 0) {
      return;
    }
    setSelected((current) => {
      const pos = visible.indexOf(current);
      if (pos !== -1) {
        return visible[Math.min(Math.max(pos + delta, 0), visible.length - 1)] ?? current;
      }
      // The selected row was hidden by a collapse: land on the nearest visible row in the move's direction.
      return delta > 0
        ? (visible.find((i) => i > current) ?? visible[visible.length - 1] ?? current)
        : (visible.findLast((i) => i < current) ?? visible[0] ?? current);
    });
    scrollSelectedIntoView();
  };

  const onKeyDown = (e: KeyboardEvent): void => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      move(1);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      move(-1);
    } else if (e.key === "Enter") {
      e.preventDefault();
      // Only ever open the row the user can see; a selection hidden by a collapse must not open blind.
      if (visibleIndices().includes(selected())) {
        openMatch(matches()[selected()]);
      }
    } else if (e.key === "Escape") {
      e.preventDefault();
      props.onClose();
    }
  };

  const toggleGroup = (path: string): void => {
    setCollapsed((s) => {
      const next = new Set(s);
      if (next.has(path)) {
        next.delete(path);
      } else {
        next.add(path);
      }
      return next;
    });
  };

  onMount(() => {
    input.focus();
    const off = onHostMessage((message) => {
      // Drop a stale reply for a query the user has since typed past (echoed query check).
      if (message.type === "find-in-files-results" && message.query === query().trim()) {
        setMatches(message.matches);
        setTruncated(message.truncated);
        setError(message.error ?? null);
        setSettled(true);
        setSelected(message.matches.length > 0 ? 0 : -1);
      }
    });
    onCleanup(() => {
      off();
      window.clearTimeout(debounceTimer);
    });
  });

  return (
    <div class="search-panel" role="search" onKeyDown={onKeyDown}>
      <div class="search-head">
        <span class="search-title">Search</span>
        <button
          type="button"
          class="search-close"
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
          value={query()}
          onInput={(e) => onInput(e.currentTarget.value)}
        />
      </div>
      <Show when={truncated()}>
        <div class="search-truncated">
          Showing the first {matches().length} matches — results truncated. Refine your search.
        </div>
      </Show>
      <Show when={error() !== null}>
        <div class="search-error">Search failed: {error()}</div>
      </Show>
      <div class="search-body" ref={listRef}>
        <Show
          when={matches().length > 0}
          fallback={
            <Show when={query().trim().length > 0 && settled() && error() === null}>
              <div class="search-empty">No results</div>
            </Show>
          }
        >
          <For each={groups()}>
            {(group) => (
              <div class="search-group">
                <button
                  type="button"
                  class="search-group-head"
                  title={group.path}
                  onClick={() => toggleGroup(group.path)}
                >
                  <span class="search-twisty" aria-hidden="true">
                    <Show when={collapsed().has(group.path)} fallback={<ChevronDown />}>
                      <ChevronRight />
                    </Show>
                  </span>
                  <span class="search-group-name">{leafName(group.path)}</span>
                  <span class="search-group-count">{group.matches.length}</span>
                </button>
                <Show when={!collapsed().has(group.path)}>
                  <For each={group.matches}>
                    {(match) => {
                      const index = (): number => matches().indexOf(match);
                      return (
                        <button
                          type="button"
                          class="search-row"
                          data-selected={index() === selected()}
                          classList={{ selected: index() === selected() }}
                          onMouseDown={(e) => {
                            e.preventDefault();
                            setSelected(index());
                            openMatch(match);
                          }}
                        >
                          <span class="search-row-line">{match.line}</span>
                          <span class="search-row-preview">{match.preview.trim()}</span>
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
    </div>
  );
}
