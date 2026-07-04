import { createEffect, createSignal, For, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { type PullRequestInfo, requestPullRequests, resolvePullRequest } from "../bridge";
import { type OpenPrTarget, parsePrRef } from "./pr-ref";

const SEARCH_DEBOUNCE_MS = 250;

// The live preview of a typed #N / pasted URL: resolving, the resolved PR, or not-found.
type Preview = "loading" | "notfound" | PullRequestInfo;

// Open Pull Request: type to search the repo's PRs (forge-side, so it scales past the default list), or paste a
// URL / type #123 to open one directly by number. Enter opens, ↑/↓ move, Esc cancels. See docs/specs/open-pr.md.
export function OpenPrPrompt(props: {
  backendId: string;
  onOpen: (target: OpenPrTarget, backendId: string) => void;
  onCancel: () => void;
}): JSX.Element {
  const [results, setResults] = createSignal<PullRequestInfo[]>([]);
  const [loading, setLoading] = createSignal(true);
  const [query, setQuery] = createSignal("");
  const [highlight, setHighlight] = createSignal(0);

  const [preview, setPreview] = createSignal<Preview | null>(null);

  const directRef = (): OpenPrTarget | null => parsePrRef(query());

  // Live-preview a direct #N / URL by resolving it on the host (debounced, latest-wins), so the row shows the
  // real title/author as you type, or "not found".
  let resolveSeq = 0;
  createEffect(() => {
    const ref = directRef();
    if (ref === null) {
      setPreview(null);
      return;
    }
    setPreview("loading");
    const mine = ++resolveSeq;
    const timer = setTimeout(() => {
      void resolvePullRequest(props.backendId, ref).then((pr) => {
        if (mine === resolveSeq) {
          setPreview(pr ?? "notfound");
        }
      });
    }, SEARCH_DEBOUNCE_MS);
    onCleanup(() => clearTimeout(timer));
  });

  // Debounced forge-side search (empty query = the recent-open default), latest-query-wins. Skipped while the
  // input is a direct #N / URL reference — that opens by number without a list.
  let seq = 0;
  createEffect(() => {
    const q = query();
    if (parsePrRef(q) !== null) {
      setResults([]);
      setLoading(false);
      return;
    }
    setLoading(true);
    const mine = ++seq;
    const timer = setTimeout(() => {
      void requestPullRequests(props.backendId, q.trim()).then((list) => {
        if (mine === seq) {
          setResults(list);
          setLoading(false);
        }
      });
    }, SEARCH_DEBOUNCE_MS);
    onCleanup(() => clearTimeout(timer));
  });

  createEffect(() => {
    const count = results().length;
    if (highlight() >= count) {
      setHighlight(count === 0 ? 0 : count - 1);
    }
  });

  const openTarget = (target: OpenPrTarget | undefined): void => {
    if (target !== undefined) {
      props.onOpen(target, props.backendId);
    }
  };
  const openResult = (pr: PullRequestInfo): void =>
    openTarget({ number: pr.number, owner: "", repo: "" });

  const onKeyDown = (event: KeyboardEvent): void => {
    const list = results();
    if (event.key === "ArrowDown" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h + 1) % list.length);
    } else if (event.key === "ArrowUp" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h <= 0 ? list.length - 1 : h - 1));
    } else if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      const ref = directRef();
      if (ref !== null) {
        openTarget(ref);
      } else {
        openTarget(list[highlight()] && { number: list[highlight()]!.number, owner: "", repo: "" });
      }
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onCancel();
    }
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onCancel()}>
        <div
          class="confirm-dialog session-prompt"
          role="dialog"
          aria-modal="true"
          aria-labelledby="open-pr-title"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title" id="open-pr-title">
            Open pull request
          </div>
          <div class="confirm-body">
            Search for a pull request, or paste its URL / type its number (e.g. #46) to open it
            directly.
          </div>
          <div class="session-prompt-field">
            <input
              class="session-prompt-input"
              type="text"
              placeholder="search, #number, or URL"
              spellcheck={false}
              autocomplete="off"
              value={query()}
              onInput={(event) => {
                setQuery(event.currentTarget.value);
                setHighlight(0);
              }}
              ref={(el) => {
                queueMicrotask(() => el.focus());
              }}
            />
            {/* A direct #N / URL reference: one row to open it by number (resolved on the host). */}
            <Show when={directRef() !== null}>
              <ul class="session-prompt-suggestions">
                <li
                  class="session-prompt-suggestion active"
                  onPointerDown={(event) => {
                    event.preventDefault();
                    openTarget(directRef() ?? undefined);
                  }}
                >
                  <span class="pr-suggestion-number">#{directRef()?.number}</span>
                  {/* Show the resolved PR's title/author once the preview lands; otherwise resolving / not-found. */}
                  <Show
                    when={
                      typeof preview() === "object" ? (preview() as PullRequestInfo) : undefined
                    }
                    fallback={
                      <span class="pr-suggestion-title">
                        {preview() === "notfound" ? "Not found in this repository" : "Resolving…"}
                      </span>
                    }
                  >
                    {(pr) => (
                      <>
                        <span class="pr-suggestion-title">{pr().title}</span>
                        <Show when={pr().draft}>
                          <span class="pr-suggestion-draft">draft</span>
                        </Show>
                        <span class="pr-suggestion-meta">@{pr().author}</span>
                      </>
                    )}
                  </Show>
                </li>
              </ul>
            </Show>
            <Show when={directRef() === null && loading()}>
              <div class="session-prompt-hint">Searching pull requests…</div>
            </Show>
            <Show when={directRef() === null && !loading() && results().length === 0}>
              <div class="session-prompt-hint">
                No matching pull requests (or no GitHub credential).
              </div>
            </Show>
            <Show when={directRef() === null && results().length > 0}>
              <ul class="session-prompt-suggestions">
                <For each={results()}>
                  {(pr, i) => (
                    <li
                      class="session-prompt-suggestion"
                      classList={{ active: i() === highlight() }}
                      title={`Open #${pr.number}`}
                      onPointerDown={(event) => {
                        event.preventDefault();
                        openResult(pr);
                      }}
                    >
                      <span class="pr-suggestion-number">#{pr.number}</span>
                      <span class="pr-suggestion-title">{pr.title}</span>
                      <Show when={pr.draft}>
                        <span class="pr-suggestion-draft">draft</span>
                      </Show>
                      <span class="pr-suggestion-meta">
                        @{pr.author}
                        <Show when={pr.headRef !== ""}> · {pr.headRef}</Show>
                      </span>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
          </div>
          <div class="session-prompt-actions">
            <button
              type="button"
              class="session-prompt-btn"
              onClick={() => props.onCancel()}
              title="Cancel (Esc)"
            >
              <span class="session-prompt-btn-label">Cancel</span>
              <span class="session-prompt-btn-key">Esc</span>
            </button>
            <button
              type="button"
              class="session-prompt-btn session-prompt-btn-primary"
              disabled={directRef() === null && results().length === 0}
              onClick={() =>
                openTarget(
                  directRef() ??
                    (results()[highlight()] && {
                      number: results()[highlight()]!.number,
                      owner: "",
                      repo: "",
                    }),
                )
              }
              title="Open the selected pull request (Enter)"
            >
              <span class="session-prompt-btn-label">Open PR</span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
