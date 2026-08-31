import { type Accessor, For, type JSX, Show } from "solid-js";
import { keyHint } from "../../commands/key-hint";
import { runCommandWithFeedback } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { reviewToModelLine } from "../diff-geometry";
import {
  buildReviewDiffPatch,
  type ReviewDiffPatch,
  type ReviewDiffRow,
} from "./review-diff-model";
import type { ReviewFileDiff, ReviewFileView } from "./review-store";

interface FilePatches {
  pending: ReviewDiffPatch;
  reviewed: ReviewDiffPatch;
}

const patchCache = new WeakMap<ReviewFileDiff, FilePatches>();
const ROWS_PER_CHUNK = 80;

export function ReviewFileSection(props: {
  displayPath: (path: string) => string;
  file: Accessor<ReviewFileView>;
  index: number;
  measure: (element: HTMLElement) => void;
  style: string;
}): JSX.Element {
  const summary = () => props.file().summary();
  const diff = () => props.file().diff();
  const patches = (): FilePatches | null => {
    const value = diff();
    if (value === null) {
      return null;
    }
    const cached = patchCache.get(value);
    if (cached !== undefined) {
      return cached;
    }
    const created = {
      pending: buildReviewDiffPatch(value.baseline, value.current),
      reviewed: buildReviewDiffPatch(value.acceptedBaseline, value.baseline),
    };
    patchCache.set(value, created);
    return created;
  };
  const openFile = (line: number): void => {
    void runCommandWithFeedback(CommandIds.reviewOpen, { path: summary().path, line });
  };
  const firstLine = (): number => patches()?.pending.hunks[0]?.newLine ?? summary().line;
  const pending = (): boolean => diff() !== null && diff()!.baseline !== diff()!.current;
  const openReviewed = (line: number): void => {
    openFile(reviewToModelLine(patches()?.pending.changes ?? [], line));
  };

  return (
    <article
      class="unified-review-file"
      data-index={props.index}
      ref={props.measure}
      style={props.style}
    >
      <header class="unified-review-file-header">
        <button
          type="button"
          class="unified-review-file-name"
          title={`Open this change in file review${keyHint(CommandIds.reviewOpen)}`}
          onClick={() => openFile(firstLine())}
        >
          {props.displayPath(summary().path)}
        </button>
        <span class="unified-review-file-stats">
          <span class="unified-review-added">+{summary().added}</span>
          <span class="unified-review-removed">−{summary().removed}</span>
        </span>
        <Show when={diff() !== null && pending()} fallback={<ReviewStatus file={props.file} />}>
          <button
            type="button"
            class="unified-review-file-action keep"
            title={`Keep file${keyHint(CommandIds.keepFile)}`}
            onClick={() =>
              void runCommandWithFeedback(CommandIds.keepFile, { path: summary().path })
            }
          >
            Keep file
          </button>
          <button
            type="button"
            class="unified-review-file-action revert"
            title={`Revert file${keyHint(CommandIds.revertFile)}`}
            onClick={() =>
              void runCommandWithFeedback(CommandIds.revertFile, { path: summary().path })
            }
          >
            Revert file
          </button>
        </Show>
      </header>
      <Show when={patches()} fallback={<div class="unified-review-notice">Loading diff…</div>}>
        {(loaded) => (
          <>
            <ReviewPatch
              patch={loaded().pending}
              label="Pending changes"
              reviewed={false}
              onOpen={openFile}
            />
            <ReviewPatch
              patch={loaded().reviewed}
              label="Reviewed changes"
              reviewed={true}
              onOpen={openReviewed}
            />
            <Show
              when={
                !loaded().pending.timedOut &&
                !loaded().reviewed.timedOut &&
                loaded().pending.hunks.length === 0 &&
                loaded().reviewed.hunks.length === 0
              }
            >
              <div class="unified-review-notice">No changes remain in this file.</div>
            </Show>
          </>
        )}
      </Show>
    </article>
  );
}

function ReviewStatus(props: { file: Accessor<ReviewFileView> }): JSX.Element {
  return (
    <span class="unified-review-status">
      {props.file().diff() === null ? "Loading…" : "Reviewed"}
    </span>
  );
}

function ReviewPatch(props: {
  patch: ReviewDiffPatch;
  label: string;
  reviewed: boolean;
  onOpen: (line: number) => void;
}): JSX.Element {
  return (
    <Show when={props.patch.timedOut || props.patch.hunks.length > 0}>
      <section class="unified-review-patch" classList={{ reviewed: props.reviewed === true }}>
        <div class="unified-review-patch-label">{props.label}</div>
        <Show
          when={!props.patch.timedOut}
          fallback={
            <div class="unified-review-notice">
              Diff calculation timed out. Open the file for focused review.
            </div>
          }
        >
          <For each={props.patch.hunks}>
            {(hunk) => (
              <div class="unified-review-hunk">
                <button
                  type="button"
                  class="unified-review-hunk-header"
                  title="Open this hunk in file review"
                  onClick={() => props.onOpen(hunk.newLine)}
                >
                  {hunk.header}
                </button>
                <For each={chunkRows(hunk.rows)}>
                  {(rows) => (
                    <div class="unified-review-row-chunk">
                      <For each={rows}>{(row) => <ReviewRow row={row} />}</For>
                    </div>
                  )}
                </For>
              </div>
            )}
          </For>
        </Show>
      </section>
    </Show>
  );
}

function chunkRows(rows: ReviewDiffRow[]): ReviewDiffRow[][] {
  const chunks: ReviewDiffRow[][] = [];
  for (let index = 0; index < rows.length; index += ROWS_PER_CHUNK) {
    chunks.push(rows.slice(index, index + ROWS_PER_CHUNK));
  }
  return chunks;
}

function ReviewRow(props: { row: ReviewDiffRow }): JSX.Element {
  const marker = (): string =>
    props.row.kind === "added" ? "+" : props.row.kind === "removed" ? "−" : " ";
  return (
    <div class={`unified-review-row ${props.row.kind}`}>
      <span class="unified-review-line-number">{props.row.oldLine ?? ""}</span>
      <span class="unified-review-line-number">{props.row.newLine ?? ""}</span>
      <span class="unified-review-marker">{marker()}</span>
      <code>{props.row.text || " "}</code>
    </div>
  );
}
