import {
  createMemo,
  createResource,
  createSignal,
  For,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import { Portal } from "solid-js/web";
import type { ClientSession } from "../bridge";
import { placeRailPopover } from "../chrome/popover-position";
import { openUrlExternal } from "../terminal/terminal-links";
import { type BlameCommit, relativeTime, shortSha } from "./blame-model";
import { type BlameCommitTarget, type BlameTarget, closeBlame } from "./blame-store";

// The panel behind a blame annotation. Its subject is the change the line came from — the one hunk of that
// commit that covers it — because that is the question a blame annotation raises. Whole commits and whole pull
// requests are one click away on the forge rather than reproduced here.
//
// Selecting a commit from the history lists re-points the panel at that commit without closing it, so walking
// a line's history is a sequence of diffs of the same area.

/** One commit in the history lists. `line` is where the tracked line sat in it; 0 for file-scoped history. */
interface HistoryCommit {
  sha: string;
  author: string;
  time: number;
  summary: string;
  line: number;
}

interface HistoryResponse {
  commits: HistoryCommit[];
  more: boolean;
  error: string | null;
}

interface HunkResponse {
  hunk: { header: string; oldStart: number; newStart: number; lines: string[] } | null;
  error: string | null;
}

interface CommitRefResponse {
  commitUrl: string | null;
  pullRequest: { number: number; title: string; url: string } | null;
  error: string | null;
}

type HistoryScope = "line" | "file";

function requestHistory(
  session: ClientSession,
  path: string,
  line: number,
): Promise<HistoryResponse> {
  return session
    .feature("git")
    .request<HistoryResponse, { path: string; line: number }>("history", { path, line });
}

export function BlamePopover(props: { target: BlameTarget }): JSX.Element {
  let panel!: HTMLDivElement;
  // Which commit the panel is showing: the blamed one until a history entry is picked.
  const [shown, setShown] = createSignal<BlameCommitTarget>(props.target.blamed);
  const [scope, setScope] = createSignal<HistoryScope>("line");

  const commit = (): BlameCommit => shown().commit;

  const [hunk] = createResource(
    () => ({ session: props.target.session, path: props.target.path, shown: shown() }),
    async (key): Promise<HunkResponse> =>
      // A commit picked from file history carries no line mapping, so there is no area of text to diff — the
      // panel says so rather than showing some other part of the commit as if it were this line's change.
      key.shown.originalLine === 0 || key.shown.commit.uncommitted
        ? { hunk: null, error: null }
        : key.session
            .feature("git")
            .request<HunkResponse, { path: string; sha: string; line: number }>("commitHunk", {
              path: key.path,
              sha: key.shown.commit.sha,
              line: key.shown.originalLine,
            }),
  );

  const [refs] = createResource(
    () => ({ session: props.target.session, sha: commit().sha, uncommitted: commit().uncommitted }),
    async (key): Promise<CommitRefResponse> =>
      key.uncommitted
        ? { commitUrl: null, pullRequest: null, error: null }
        : key.session
            .feature("git")
            .request<CommitRefResponse, { sha: string }>("commitRef", { sha: key.sha }),
  );

  const [history] = createResource(
    () => ({
      session: props.target.session,
      path: props.target.path,
      line: scope() === "line" ? props.target.line : 0,
    }),
    (key): Promise<HistoryResponse> => requestHistory(key.session, key.path, key.line),
  );

  // The hunk line the popover was opened from, so the change reads against the code it explains. Counted down
  // the hunk's post-image, which is the side the blamed line lives on.
  const focusIndex = createMemo(() => {
    const body = hunk()?.hunk;
    if (body === undefined || body === null) {
      return -1;
    }
    let line = body.newStart;
    for (let index = 0; index < body.lines.length; index++) {
      const marker = body.lines[index]?.[0] ?? " ";
      if (marker === "-" || marker === "\\") {
        continue;
      }
      if (line === shown().originalLine) {
        return index;
      }
      line++;
    }
    return -1;
  });

  const positionPanel = (): void => {
    const bounds = panel.getBoundingClientRect();
    const position = placeRailPopover(
      props.target.anchor,
      { width: bounds.width, height: bounds.height },
      { width: window.innerWidth, height: window.innerHeight },
    );
    panel.style.left = `${position.left}px`;
    panel.style.top = `${position.top}px`;
    panel.style.visibility = "visible";
  };

  const onPointerDown = (event: PointerEvent): void => {
    if (!(event.target as HTMLElement).closest(".weavie-blame-popover, .weavie-blame")) {
      closeBlame();
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      event.stopPropagation();
      closeBlame();
    }
  };

  let panelObserver!: ResizeObserver;
  onMount(() => {
    window.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("resize", positionPanel);
    panelObserver = new ResizeObserver(positionPanel);
    panelObserver.observe(panel);
    positionPanel();
    // Focus the panel so Escape and Tab reach it rather than the editor underneath.
    panel.focus();
  });
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown);
    window.removeEventListener("resize", positionPanel);
    panelObserver.disconnect();
  });

  const now = Date.now() / 1000;

  return (
    <Portal>
      <div
        class="weavie-blame-popover"
        ref={panel}
        role="dialog"
        aria-label="Git blame"
        tabindex="0"
        style={{ visibility: "hidden" }}
        onKeyDown={onKeyDown}
      >
        <div class="weavie-blame-head">
          <div class="weavie-blame-subject">
            {commit().uncommitted ? "Uncommitted changes" : commit().summary}
          </div>
          <div class="weavie-blame-meta">
            <Show when={!commit().uncommitted}>
              <span>{commit().author}</span>
              <span>{relativeTime(commit().time, now)}</span>
              <span class="weavie-blame-sha">{shortSha(commit().sha)}</span>
            </Show>
            <Show when={commit().uncommitted}>
              <span>Not committed yet — this line is only in your working tree.</span>
            </Show>
            <div class="weavie-blame-links">
              <Show when={refs()?.pullRequest}>
                {(pullRequest) => (
                  <button
                    type="button"
                    class="weavie-blame-link"
                    title={`Open PR #${pullRequest().number} — ${pullRequest().title}`}
                    onClick={() => openUrlExternal(pullRequest().url)}
                  >
                    PR #{pullRequest().number} ↗
                  </button>
                )}
              </Show>
              <Show when={refs()?.commitUrl}>
                {(url) => (
                  <button
                    type="button"
                    class="weavie-blame-link"
                    title="Open the whole commit on your Git provider"
                    onClick={() => openUrlExternal(url())}
                  >
                    Commit ↗
                  </button>
                )}
              </Show>
            </div>
          </div>
        </div>

        <Show
          when={hunk()?.hunk}
          fallback={
            <div class="weavie-blame-note">
              {hunk.loading
                ? "Loading the change…"
                : (hunk()?.error ??
                  (commit().uncommitted
                    ? "Save and commit the file to see this line's change here."
                    : "This commit changed the file elsewhere, not this line."))}
            </div>
          }
        >
          {(body) => (
            <div class="weavie-blame-hunk">
              <div class="weavie-blame-hunk-line weavie-blame-context">{body().header}</div>
              <For each={body().lines}>
                {(line, index) => (
                  <div
                    class={`weavie-blame-hunk-line ${hunkLineClass(line)}${
                      index() === focusIndex() ? " weavie-blame-focus" : ""
                    }`}
                  >
                    {line === "" ? " " : line}
                  </div>
                )}
              </For>
            </div>
          )}
        </Show>

        <div class="weavie-blame-history">
          <div class="weavie-blame-tabs" role="tablist">
            <button
              type="button"
              role="tab"
              class="weavie-blame-tab"
              aria-selected={scope() === "line"}
              onClick={() => setScope("line")}
            >
              This line
            </button>
            <button
              type="button"
              role="tab"
              class="weavie-blame-tab"
              aria-selected={scope() === "file"}
              onClick={() => setScope("file")}
            >
              This file
            </button>
          </div>
          <div class="weavie-blame-list">
            <Show
              when={(history()?.commits.length ?? 0) > 0}
              fallback={
                <div class="weavie-blame-note">
                  {history.loading
                    ? "Loading history…"
                    : (history()?.error ??
                      (scope() === "line"
                        ? "No other commit has changed this line."
                        : "No other commit has changed this file."))}
                </div>
              }
            >
              <For each={history()?.commits ?? []}>
                {(entry) => (
                  <button
                    type="button"
                    class="weavie-blame-entry"
                    aria-current={entry.sha === commit().sha}
                    title={`${entry.author} — ${entry.summary}`}
                    onClick={() =>
                      setShown({
                        commit: {
                          sha: entry.sha,
                          author: entry.author,
                          email: "",
                          time: entry.time,
                          summary: entry.summary,
                          uncommitted: false,
                        },
                        originalLine: entry.line,
                      })
                    }
                  >
                    <span class="weavie-blame-entry-when">{relativeTime(entry.time, now)}</span>
                    <span class="weavie-blame-entry-summary">{entry.summary}</span>
                  </button>
                )}
              </For>
              <Show when={history()?.more === true}>
                <div class="weavie-blame-note">
                  Older commits aren't listed — open the file's history on your Git provider for the
                  rest.
                </div>
              </Show>
            </Show>
          </div>
        </div>
      </div>
    </Portal>
  );
}

function hunkLineClass(line: string): string {
  switch (line[0]) {
    case "+":
      return "weavie-blame-added";
    case "-":
      return "weavie-blame-removed";
    default:
      return "weavie-blame-context";
  }
}
