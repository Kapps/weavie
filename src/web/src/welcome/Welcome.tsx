import { ArrowRight, FolderClosed, FolderOpen, Sparkles } from "lucide-solid";
import { createSignal, For, type JSX, Show } from "solid-js";
import { hostConnection, LOCAL_BACKEND_ID } from "../bridge";
import { WeavieIcon } from "../chrome/WeavieIcon";
import { GettingStarted } from "../getting-started/GettingStarted";

// The empty-state screen: app mark + wordmark, Getting Started until setup is done, then an Open Folder action and
// the recent-workspaces list. Config arrives as window.__WEAVIE_WELCOME__; the open actions publish `window.menu`
// events the host routes to open-folder / open-recent.
export function Welcome(): JSX.Element {
  const recents = (): string[] => window.__WEAVIE_WELCOME__?.recents ?? [];
  const [settingUp, setSettingUp] = createSignal(
    window.__WEAVIE_WELCOME__?.setupCompleted === false,
  );
  const openFolder = (): void =>
    hostConnection(LOCAL_BACKEND_ID)
      ?.host.feature("window")
      .publish("menu", { action: "open-folder" });
  const openRecent = (path: string): void =>
    hostConnection(LOCAL_BACKEND_ID)
      ?.host.feature("window")
      .publish("menu", { action: "open-recent", path });

  return (
    <div class="welcome">
      <main class="welcome-inner">
        <header class="welcome-head">
          <span class="welcome-mark" aria-hidden="true">
            <WeavieIcon />
          </span>
          <div class="welcome-titles">
            <h1 class="welcome-wordmark">weavie</h1>
            <p class="welcome-tagline">
              {settingUp() ? "Let's set things up." : "Open a folder to start a workspace."}
            </p>
          </div>
        </header>

        <Show when={settingUp()}>
          <GettingStarted onDone={() => setSettingUp(false)} />
        </Show>
        <Show when={!settingUp()}>
          <div class="welcome-actions">
            <button type="button" class="welcome-open" onClick={openFolder}>
              <FolderOpen size="1.1em" aria-hidden="true" />
              <span>Open Folder…</span>
            </button>
            <button type="button" class="welcome-setup" onClick={() => setSettingUp(true)}>
              <Sparkles size="1em" aria-hidden="true" />
              <span>Getting Started</span>
            </button>
          </div>

          <section class="welcome-recent">
            <h2 class="welcome-recent-label">Recent</h2>
            <Show
              when={recents().length > 0}
              fallback={
                <p class="welcome-empty">No recent folders yet — open one to get started.</p>
              }
            >
              <ul class="welcome-list">
                <For each={recents()}>
                  {(path) => (
                    <li>
                      <button type="button" class="welcome-row" onClick={() => openRecent(path)}>
                        <FolderClosed size="1.15em" class="welcome-row-icon" aria-hidden="true" />
                        <span class="welcome-row-text">
                          <span class="welcome-row-name">{folderLeaf(path)}</span>
                          <span class="welcome-row-path">{path}</span>
                        </span>
                        <ArrowRight size="1em" class="welcome-row-go" aria-hidden="true" />
                      </button>
                    </li>
                  )}
                </For>
              </ul>
            </Show>
          </section>
        </Show>
      </main>
    </div>
  );
}

// Leaf folder name for the row title, tolerating either separator and a trailing slash; full path if separatorless.
function folderLeaf(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, "");
  const cut = Math.max(trimmed.lastIndexOf("\\"), trimmed.lastIndexOf("/"));
  return cut >= 0 ? trimmed.slice(cut + 1) || trimmed : trimmed;
}
