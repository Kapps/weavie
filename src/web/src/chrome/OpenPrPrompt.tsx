import { For, type JSX, Show, createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { type PullRequestInfo, requestPullRequests } from "../bridge";

// Open Pull Request: pick one of the repo's open PRs to check out as a session. Loads the chosen backend's PRs,
// filters by a typed query (number / title / author / branch), and opens the highlighted one — Enter opens,
// ↑/↓ move, Esc cancels. Mirrors NewSessionPrompt's keyboard-first shape. See docs/specs/open-pr.md.
export function OpenPrPrompt(props: {
  backendId: string;
  onOpen: (pr: PullRequestInfo, backendId: string) => void;
  onCancel: () => void;
}): JSX.Element {
  const [prs, setPrs] = createSignal<PullRequestInfo[]>([]);
  const [loading, setLoading] = createSignal(true);
  const [query, setQuery] = createSignal("");
  const [highlight, setHighlight] = createSignal(0);

  onMount(() => {
    void requestPullRequests(props.backendId).then((list) => {
      setPrs(list);
      setLoading(false);
    });
  });

  const matches = (pr: PullRequestInfo, q: string): boolean =>
    `#${pr.number} ${pr.title} ${pr.author} ${pr.headRef}`.toLowerCase().includes(q);

  const filtered = (): PullRequestInfo[] => {
    const q = query().trim().toLowerCase();
    return q.length === 0 ? prs() : prs().filter((pr) => matches(pr, q));
  };

  // Keep the highlight within the (re-filtered) list so Enter always has a valid target.
  createEffect(() => {
    const count = filtered().length;
    if (highlight() >= count) {
      setHighlight(count === 0 ? 0 : count - 1);
    }
  });

  const open = (pr: PullRequestInfo | undefined): void => {
    if (pr !== undefined) {
      props.onOpen(pr, props.backendId);
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    const list = filtered();
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
      open(list[highlight()]);
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
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title">Open pull request</div>
          <div class="confirm-body">
            Check out a pull request's branch as a session. Pick one to open.
          </div>
          <div class="session-prompt-field">
            <input
              class="session-prompt-input"
              type="text"
              placeholder="filter pull requests"
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
            <Show when={loading()}>
              <div class="session-prompt-hint">Loading pull requests…</div>
            </Show>
            <Show when={!loading() && prs().length === 0}>
              <div class="session-prompt-hint">
                No open pull requests (or no GitHub credential).
              </div>
            </Show>
            <Show when={!loading() && filtered().length > 0}>
              <ul class="session-prompt-suggestions">
                <For each={filtered()}>
                  {(pr, i) => (
                    <li
                      class="session-prompt-suggestion"
                      classList={{ active: i() === highlight() }}
                      title={`${pr.headRef} — open #${pr.number}`}
                      onPointerDown={(event) => {
                        event.preventDefault();
                        open(pr);
                      }}
                    >
                      <span class="pr-suggestion-number">#{pr.number}</span>
                      <span class="pr-suggestion-title">{pr.title}</span>
                      <Show when={pr.draft}>
                        <span class="pr-suggestion-draft">draft</span>
                      </Show>
                      <span class="pr-suggestion-meta">
                        @{pr.author} · {pr.headRef}
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
              disabled={filtered().length === 0}
              onClick={() => open(filtered()[highlight()])}
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
