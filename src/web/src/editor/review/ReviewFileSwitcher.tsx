import { createMemo, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { createFileFinder, rankFiles, splitPath } from "../../chrome/file-search";
import { registerFloatingPanel } from "../../chrome/floating-panels";
import { highlightSlice } from "../../chrome/highlight";
import { dismissOnOutsideInteraction } from "../../chrome/popover-dismiss";
import { createListNavigation } from "../../list-navigation";
import { DiffStats } from "./DiffStats";
import { type ReviewFileView, reviewProgress } from "./review-store";

/** A filterable list of the review's files; choosing one jumps the review to it. */
export function ReviewFileSwitcher(props: {
  files: () => ReviewFileView[];
  current: () => number | undefined;
  displayPath: (path: string) => string;
  onChoose: (index: number) => void;
  /** Dismissed by clicking elsewhere, which keeps the focus where the user put it. */
  onClose: () => void;
  /** Dismissed from the keyboard, which returns focus to the review. */
  onCancel: () => void;
}): JSX.Element {
  let input: HTMLInputElement | undefined;
  const [query, setQuery] = createSignal("");
  const rows = createMemo(() =>
    props.files().map((file, index) => ({
      index,
      file,
      row: splitPath(props.displayPath(file.summary().path), ""),
    })),
  );
  const finder = createMemo(() => createFileFinder(rows().map((entry) => entry.row)));
  const ranked = createMemo(() => {
    const q = query().trim();
    if (q.length === 0) return rows().map((entry) => ({ ...entry, positions: undefined }));
    const byPath = new Map(rows().map((entry) => [entry.row.abs, entry]));
    return rankFiles(finder(), q, [], null).matches.map((match) => ({
      ...byPath.get(match.row.abs)!,
      positions: match.positions,
    }));
  });
  // Files grouped under their folder, folders in order of their first (best-ranked) file.
  const groups = createMemo(() => {
    const byDir = new Map<string, ReturnType<typeof ranked>>();
    for (const entry of ranked()) {
      const group = byDir.get(entry.row.dir);
      if (group === undefined) byDir.set(entry.row.dir, [entry]);
      else group.push(entry);
    }
    return [...byDir].map(([dir, entries]) => ({ dir, entries }));
  });
  const results = createMemo(() => groups().flatMap((group) => group.entries));
  const order = createMemo(() => new Map(results().map((entry, index) => [entry, index])));
  const reviewed = (): number =>
    props.files().filter((file) => reviewProgress(file).fraction === 1).length;

  const nav = createListNavigation({
    count: () => results().length,
    edges: "wrap",
    initialIndex: Math.max(
      0,
      results().findIndex((entry) => entry.index === props.current()),
    ),
    acceptKeys: ["Enter"],
    onAccept: (index) => {
      const entry = results()[index];
      if (entry !== undefined) props.onChoose(entry.index);
    },
    onDismiss: () => props.onCancel(),
  });

  onMount(() => {
    input?.focus();
    nav.reveal("nearest");
    // The filename buttons toggle the list themselves.
    dismissOnOutsideInteraction(
      ".unified-review-switcher, .unified-review-file-name",
      props.onClose,
    );
    onCleanup(registerFloatingPanel("review-file-switcher", props.onCancel, "popover").dispose);
  });

  return (
    <div class="unified-review-switcher">
      <input
        ref={input}
        type="text"
        placeholder={`Filter ${props.files().length} changed files…`}
        value={query()}
        spellcheck={false}
        autocomplete="off"
        onInput={(event) => {
          setQuery(event.currentTarget.value);
          nav.setIndex(0);
        }}
        onKeyDown={nav.onKeyDown}
      />
      <div class="unified-review-switcher-list" role="listbox">
        <Show
          when={results().length > 0}
          fallback={<div class="unified-review-switcher-empty">No matching files.</div>}
        >
          <For each={groups()}>
            {(group) => (
              <>
                <div class="unified-review-switcher-dir">{group.dir || "./"}</div>
                <For each={group.entries}>
                  {(entry) => {
                    const index = () => order().get(entry)!;
                    return (
                      <button
                        {...nav.row(index())}
                        type="button"
                        role="option"
                        class="unified-review-switcher-row"
                        classList={{
                          selected: index() === nav.index(),
                          current: entry.index === props.current(),
                        }}
                        aria-selected={index() === nav.index()}
                        title={entry.row.rel}
                        onClick={() => props.onChoose(entry.index)}
                      >
                        <span
                          class="unified-review-switcher-state"
                          classList={{ reviewed: reviewProgress(entry.file).fraction === 1 }}
                        />
                        <span class="unified-review-switcher-leaf">
                          {highlightSlice(entry.row.leaf, entry.positions, entry.row.leafStart)}
                        </span>
                        <DiffStats
                          added={entry.file.summary().added}
                          removed={entry.file.summary().removed}
                        />
                      </button>
                    );
                  }}
                </For>
              </>
            )}
          </For>
        </Show>
      </div>
      <footer>
        <span>↑↓ move · ↵ jump · Esc close</span>
        <span>
          {reviewed()} of {props.files().length} reviewed
        </span>
      </footer>
    </div>
  );
}
