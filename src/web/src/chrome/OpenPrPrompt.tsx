import { createSignal, For, type JSX, Show } from "solid-js";
import type { ClientSession, PullRequestInfo } from "../bridge";
import { createListNavigation } from "../list-navigation";
import { ModalShell, PromptButton } from "./ModalShell";
import { createPrLookup } from "./pr-lookup";
import { type OpenPrTarget, parsePrRef } from "./pr-ref";

// Open Pull Request: type to search the PRs of one session's repo (forge-side, so it scales past the default
// list), or paste a URL / type #123 to open one directly by number. Every lookup and the open go to the session
// the prompt was opened for, whichever session is selected by then. See docs/specs/open-pr.md.
export function OpenPrPrompt(props: {
  session: ClientSession;
  onOpen: (target: OpenPrTarget) => void;
  onCancel: () => void;
}): JSX.Element {
  const [query, setQuery] = createSignal("");
  const lookup = createPrLookup(props.session, query);
  const directRef = (): OpenPrTarget | null => parsePrRef(query());
  const results = (): PullRequestInfo[] => {
    const current = lookup();
    return current.status === "listed" ? current.results : [];
  };
  const failure = (): string | null => {
    const current = lookup();
    return current.status === "failed" ? current.message : null;
  };
  const resolved = (): PullRequestInfo | null | undefined => {
    const current = lookup();
    return current.status === "resolved" ? current.pr : undefined;
  };

  const openTarget = (target: OpenPrTarget | undefined): void => {
    if (target !== undefined) {
      props.onOpen(target);
    }
  };
  const targetOf = (pr: PullRequestInfo | undefined): OpenPrTarget | undefined =>
    pr === undefined ? undefined : { number: pr.number, owner: "", repo: "" };
  const nav = createListNavigation({
    count: () => results().length,
    edges: "wrap",
    initialIndex: 0,
    acceptKeys: ["Enter"],
    onAccept: () => openTarget(selected()),
    onDismiss: () => props.onCancel(),
  });
  // A typed #N / URL beats the highlighted row: Enter opens it directly, list or not.
  const selected = (): OpenPrTarget | undefined => directRef() ?? targetOf(results()[nav.index()]);

  return (
    <ModalShell
      title="Open pull request"
      actions={
        <>
          <PromptButton
            label="Cancel"
            shortcut="Esc"
            title="Cancel (Esc)"
            onClick={props.onCancel}
          />
          <PromptButton
            label="Open PR"
            shortcut="Enter"
            title="Open the selected pull request (Enter)"
            disabled={directRef() === null && results().length === 0}
            onClick={() => openTarget(selected())}
            primary
          />
        </>
      }
      onDismiss={props.onCancel}
      onKeyDown={nav.onKeyDown}
      class="session-prompt"
    >
      <div class="session-prompt-field">
        <input
          class="session-prompt-input ui-field ui-field-mono"
          type="text"
          placeholder="search, #number, or URL"
          spellcheck={false}
          autocomplete="off"
          value={query()}
          onInput={(event) => {
            setQuery(event.currentTarget.value);
            nav.setIndex(0);
          }}
          ref={(el) => {
            queueMicrotask(() => el.focus());
          }}
        />
        {/* A direct #N / URL reference: one row to open it by number (resolved on the host). */}
        <Show when={directRef() !== null && failure() === null}>
          <ul class="session-prompt-suggestions">
            <li
              class="session-prompt-suggestion ui-row active"
              onPointerDown={(event) => {
                event.preventDefault();
                openTarget(directRef() ?? undefined);
              }}
            >
              <span class="pr-suggestion-number">#{directRef()?.number}</span>
              {/* The resolved PR's title/author once it lands; otherwise resolving / not-found. */}
              <Show
                when={resolved()}
                fallback={
                  <span class="pr-suggestion-title">
                    {resolved() === null ? "Not found in this repository" : "Resolving…"}
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
        <Show when={directRef() === null && results().length > 0}>
          <ul class="session-prompt-suggestions">
            <For each={results()}>
              {(pr, i) => (
                <li
                  {...nav.row(i())}
                  class="session-prompt-suggestion ui-row"
                  classList={{ active: i() === nav.index() }}
                  title={`Open #${pr.number}`}
                  onPointerDown={(event) => {
                    event.preventDefault();
                    openTarget(targetOf(pr));
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
      {/* Under the field, where no suggestion list is open: a failed lookup lists nothing. */}
      <div class="session-prompt-hint">
        <Show when={failure()}>
          {(message) => (
            <span class="session-prompt-error" role="alert">
              {message()}
            </span>
          )}
        </Show>
        <Show when={directRef() === null && lookup().status === "loading"}>
          Searching pull requests…
        </Show>
        <Show when={directRef() === null && lookup().status === "listed" && results().length === 0}>
          No matching pull requests (or no GitHub credential).
        </Show>
      </div>
    </ModalShell>
  );
}
