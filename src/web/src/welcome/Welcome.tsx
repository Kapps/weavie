import { ArrowRight, FolderClosed, FolderOpen } from "lucide-solid";
import { For, type JSX, Show } from "solid-js";
import { postToHost } from "../bridge";
import { WeavieIcon } from "../chrome/WeavieIcon";

// The empty-state screen: app mark + wordmark, an Open Folder action, and the recent-workspaces list.
// Lives in the shared web app (not per-OS chrome) so every host renders the same thing. Recents arrive as
// window.__WEAVIE_WELCOME__; both actions post the `menu-action` messages the host routes to open-folder /
// open-recent.
export function Welcome(): JSX.Element {
  const recents = (): string[] => window.__WEAVIE_WELCOME__?.recents ?? [];
  const openFolder = (): void => postToHost({ type: "menu-action", action: "open-folder" });
  const openRecent = (path: string): void =>
    postToHost({ type: "menu-action", action: "open-recent", path });

  return (
    <div class="welcome">
      <main class="welcome-inner">
        <header class="welcome-head">
          <span class="welcome-mark" aria-hidden="true">
            <WeavieIcon />
          </span>
          <div class="welcome-titles">
            <h1 class="welcome-wordmark">weavie</h1>
            <p class="welcome-tagline">Open a folder to start a workspace.</p>
          </div>
        </header>

        <button type="button" class="welcome-open" onClick={openFolder}>
          <FolderOpen size="1.1em" aria-hidden="true" />
          <span>Open Folder…</span>
        </button>

        <section class="welcome-recent">
          <h2 class="welcome-recent-label">Recent</h2>
          <Show
            when={recents().length > 0}
            fallback={<p class="welcome-empty">No recent folders yet — open one to get started.</p>}
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
      </main>
    </div>
  );
}

// Leaf folder name for the row title, tolerating either separator and a trailing slash; falls back to
// the full path when there's no separator.
function folderLeaf(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, "");
  const cut = Math.max(trimmed.lastIndexOf("\\"), trimmed.lastIndexOf("/"));
  return cut >= 0 ? trimmed.slice(cut + 1) || trimmed : trimmed;
}
