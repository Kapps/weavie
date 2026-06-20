import { X } from "lucide-solid";
import { For, type JSX, createEffect, createSignal, onMount } from "solid-js";

// One changed file in a navigator list: path + the line counts the host computed, plus (review mode) the
// 1-based line of its first change, so opening the file lands the editor on that first diff.
export interface ChangeFile {
  path: string;
  name: string;
  added: number;
  removed: number;
  line?: number;
}

// A changed-files navigator: a keyboard-drivable list (↑/↓ move the cursor + focus a row, Enter opens it, Esc
// closes the panel). Selecting a file opens it in the live editor with its diff shown inline — there's no
// standalone diff viewer, so this is a jump list, not its own diff editor. Shared by the session-changes feed
// and the post-turn review list; only the title and which diff each row opens differ (the caller decides).
//
// Focus lives on the row buttons (native, focusable), so arrow handling bubbles to the <ul> and stays scoped
// to the panel — moving into the editor hands the arrow keys back to it. No tabindex on a static element.
export default function ChangesPanel(props: {
  title: string;
  files: ChangeFile[];
  onSelect: (file: ChangeFile) => void;
  onClose: () => void;
  autoFocus?: boolean;
}): JSX.Element {
  const buttons: HTMLButtonElement[] = [];
  const [selected, setSelected] = createSignal(0);

  // Keep the cursor in range as the list shrinks under us (a new turn, or rows reviewed away).
  createEffect(() => {
    const max = Math.max(0, props.files.length - 1);
    if (selected() > max) {
      setSelected(max);
    }
  });

  // Focus the first row when the navigator opens, so ↑/↓/Enter work without a click first.
  onMount(() => {
    if (props.autoFocus === true) {
      buttons[selected()]?.focus();
    }
  });

  const focusRow = (index: number): void => {
    const clamped = Math.max(0, Math.min(index, props.files.length - 1));
    setSelected(clamped);
    buttons[clamped]?.focus();
  };

  // Enter is handled by each row button's native click; we own ↑/↓ (move the cursor) and Esc (close).
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      focusRow(selected() + 1);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      focusRow(selected() - 1);
    } else if (event.key === "Escape") {
      event.preventDefault();
      props.onClose();
    }
  };

  return (
    <div class="changes-panel changes-panel-nav">
      <div class="changes-head">
        <span class="changes-title">{props.title}</span>
        <button type="button" class="changes-close" onClick={() => props.onClose()}>
          <X />
        </button>
      </div>
      <ul class="changes-list" onKeyDown={onKeyDown}>
        <For each={props.files} fallback={<li class="changes-empty">No changes yet.</li>}>
          {(file, index) => (
            <li class="changes-row">
              <button
                type="button"
                ref={(el) => {
                  buttons[index()] = el;
                }}
                classList={{ "changes-item": true, active: selected() === index() }}
                onClick={() => {
                  setSelected(index());
                  props.onSelect(file);
                }}
                onFocus={() => setSelected(index())}
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
