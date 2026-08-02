import { createEffect, createSignal, For, type JSX, Show } from "solid-js";
import { connectedBackends } from "../bridge";
import { type RailSession, STATUS_SHORT } from "../chrome/session-store";

/** The compact home surface: all sessions plus a prompt-first path to a new worktree session. */
export function SessionInbox(props: {
  sessions: RailSession[];
  initialBackendId: string;
  initialProviderId: "claude" | "codex";
  onOpen: (session: RailSession) => Promise<boolean>;
  onCreate: (prompt: string, backendId: string, providerId: "claude" | "codex") => Promise<boolean>;
  onMore: () => void;
  moreTitle: string;
}): JSX.Element {
  const [prompt, setPrompt] = createSignal("");
  const [backendId, setBackendId] = createSignal(props.initialBackendId);
  const [providerId, setProviderId] = createSignal<"claude" | "codex">(props.initialProviderId);
  const [submitting, setSubmitting] = createSignal(false);

  createEffect(() => {
    if (!connectedBackends().some((backend) => backend.id === backendId())) {
      setBackendId("local");
    }
  });

  const submit = async (): Promise<void> => {
    const text = prompt().trim();
    if (text.length === 0 || submitting()) {
      return;
    }
    setSubmitting(true);
    if (await props.onCreate(text, backendId(), providerId())) {
      setPrompt("");
    }
    setSubmitting(false);
  };

  return (
    <main class="session-inbox">
      <header class="session-inbox-header">
        <img src="/weavie.png" width="32" height="32" alt="" />
        <div>
          <h1>Sessions</h1>
          <span>Pick up where your agents left off</span>
        </div>
      </header>

      <form
        class="session-composer"
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <textarea
          aria-label="Prompt for a new session"
          placeholder="Start a new session…"
          rows={3}
          value={prompt()}
          onInput={(event) => setPrompt(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter" && !event.shiftKey) {
              event.preventDefault();
              void submit();
            }
          }}
        />
        <div class="session-composer-options">
          <select
            aria-label="Session location"
            value={backendId()}
            onChange={(event) => setBackendId(event.currentTarget.value)}
          >
            <For each={connectedBackends()}>
              {(backend) => (
                <option value={backend.id}>{backend.isLocal ? "Local" : backend.name}</option>
              )}
            </For>
          </select>
          <select
            aria-label="Agent provider"
            value={providerId()}
            onChange={(event) => setProviderId(event.currentTarget.value as "claude" | "codex")}
          >
            <option value="claude">Claude Code</option>
            <option value="codex">Codex</option>
          </select>
          <button
            type="button"
            class="session-composer-more"
            title={props.moreTitle}
            onClick={() => props.onMore()}
          >
            More…
          </button>
          <button
            type="submit"
            class="session-composer-submit"
            disabled={prompt().trim().length === 0 || submitting()}
          >
            {submitting() ? "Starting…" : "Start"}
          </button>
        </div>
      </form>

      <section class="session-inbox-list" aria-label="Available sessions">
        <Show
          when={props.sessions.length > 0}
          fallback={<p class="session-inbox-empty">No sessions yet.</p>}
        >
          <For each={props.sessions}>
            {(session) => (
              <button
                type="button"
                class={`session-inbox-row status-${session.status}`}
                classList={{ active: session.active, offline: session.offline }}
                disabled={session.pending || session.offline}
                ref={(element) => element.style.setProperty("--chip-hue", String(session.hue))}
                onClick={() => void props.onOpen(session)}
              >
                <span class="session-inbox-monogram">{session.monogram}</span>
                <span class="session-inbox-details">
                  <strong>{session.label}</strong>
                  <span>
                    {session.locationName} ·{" "}
                    {session.providerId === "codex" ? "Codex" : "Claude Code"}
                  </span>
                </span>
                <span class="session-inbox-state">
                  <Show when={session.loaded}>
                    <span class="session-status" />
                  </Show>
                  {session.offline
                    ? "Reconnecting"
                    : session.loaded
                      ? STATUS_SHORT[session.status]
                      : "Unloaded"}
                </span>
              </button>
            )}
          </For>
        </Show>
      </section>
    </main>
  );
}
