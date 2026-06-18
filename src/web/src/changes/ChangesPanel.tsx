import { X } from "lucide-solid";
import { For, type JSX } from "solid-js";

// One changed file in the session list: path + the line counts the host computed.
export interface ChangeFile {
  path: string;
  name: string;
  added: number;
  removed: number;
}

// The session-changes navigator: a list of files changed this session (with +/- counts). Selecting one opens
// it in the live editor with its diff shown inline (there's no standalone diff viewer) — so this is a jump
// list, not its own diff editor.
export default function ChangesPanel(props: {
  files: ChangeFile[];
  currentFile: string | null;
  onSelect: (path: string) => void;
  onClose: () => void;
}): JSX.Element {
  return (
    <div class="changes-panel changes-panel-nav">
      <div class="changes-head">
        <span class="changes-title">Session changes ({props.files.length})</span>
        <button type="button" class="changes-close" onClick={() => props.onClose()}>
          <X />
        </button>
      </div>
      <ul class="changes-list">
        <For
          each={props.files}
          fallback={<li class="changes-empty">No changes yet this session.</li>}
        >
          {(file) => (
            <li class="changes-row">
              <button
                type="button"
                classList={{ "changes-item": true, active: props.currentFile === file.path }}
                onClick={() => props.onSelect(file.path)}
              >
                <span class="cf-name" title={file.path}>
                  {file.name}
                </span>
                <span class="cf-stat">
                  <span class="cf-add">+{file.added}</span>
                  <span class="cf-del">−{file.removed}</span>
                </span>
              </button>
            </li>
          )}
        </For>
      </ul>
    </div>
  );
}
