import { X } from "lucide-solid";
import { For, type JSX, Show, createMemo } from "solid-js";

// One changed file in the session list: path + the line counts the host computed.
export interface ChangeFile {
  path: string;
  name: string;
  added: number;
  removed: number;
}

// One file's session diff: its baseline (content at first touch) vs. its current content.
export interface ChangeDiff {
  path: string;
  name: string;
  baseline: string;
  current: string;
}

type Row = { kind: "ctx" | "add" | "del"; sign: string; text: string };

// Above this (rows × cols) the O(n·m) LCS is too costly for the page; show every line as removed-then-added
// instead. Rare for source files; keeps a pathological paste from freezing the UI.
const LCS_CELL_CAP = 2_000_000;

function splitLines(text: string): string[] {
  return text.length === 0 ? [] : text.replace(/\r\n?/g, "\n").split("\n");
}

// A unified line diff via an LCS backtrack — enough for a readable "what changed this session" view.
function diffLines(before: string, after: string): Row[] {
  const a = splitLines(before);
  const b = splitLines(after);
  const n = a.length;
  const m = b.length;

  if (n * m > LCS_CELL_CAP) {
    return [
      ...a.map((text): Row => ({ kind: "del", sign: "−", text })),
      ...b.map((text): Row => ({ kind: "add", sign: "+", text })),
    ];
  }

  // dp[i][j] = LCS length of a[i:] and b[j:]. Indexing is bounds-guaranteed by the loop ranges, so the
  // non-null assertions satisfy noUncheckedIndexedAccess at no runtime cost.
  const dp: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    const row = dp[i]!;
    const next = dp[i + 1]!;
    const ai = a[i]!;
    for (let j = m - 1; j >= 0; j--) {
      row[j] = ai === b[j]! ? next[j + 1]! + 1 : Math.max(next[j]!, row[j + 1]!);
    }
  }

  const rows: Row[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    const ai = a[i]!;
    const bj = b[j]!;
    if (ai === bj) {
      rows.push({ kind: "ctx", sign: " ", text: ai });
      i++;
      j++;
    } else if (dp[i + 1]![j]! >= dp[i]![j + 1]!) {
      rows.push({ kind: "del", sign: "−", text: ai });
      i++;
    } else {
      rows.push({ kind: "add", sign: "+", text: bj });
      j++;
    }
  }
  while (i < n) {
    rows.push({ kind: "del", sign: "−", text: a[i]! });
    i++;
  }
  while (j < m) {
    rows.push({ kind: "add", sign: "+", text: b[j]! });
    j++;
  }
  return rows;
}

function UnifiedDiff(props: { diff: ChangeDiff }): JSX.Element {
  const rows = createMemo(() => diffLines(props.diff.baseline, props.diff.current));
  return (
    <div class="udiff">
      <For each={rows()}>
        {(row) => (
          <div class={`udiff-row ${row.kind}`}>
            <span class="udiff-sign">{row.sign}</span>
            <span class="udiff-text">{row.text === "" ? " " : row.text}</span>
          </div>
        )}
      </For>
    </div>
  );
}

// The session changes overlay: a file list (with +/- counts) beside the selected file's unified diff.
// Self-contained — no Monaco, no layout pane — so it never collides with the editor or the pane tree.
export default function ChangesPanel(props: {
  files: ChangeFile[];
  diff: ChangeDiff | null;
  onSelect: (path: string) => void;
  onClose: () => void;
}): JSX.Element {
  return (
    <div class="changes-panel">
      <div class="changes-head">
        <span class="changes-title">Session changes ({props.files.length})</span>
        <button type="button" class="changes-close" onClick={() => props.onClose()}>
          <X />
        </button>
      </div>
      <div class="changes-body">
        <ul class="changes-list">
          <For
            each={props.files}
            fallback={<li class="changes-empty">No changes yet this session.</li>}
          >
            {(file) => (
              <li class="changes-row">
                <button
                  type="button"
                  classList={{ "changes-item": true, active: props.diff?.path === file.path }}
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
        <div class="changes-diff">
          <Show
            when={props.diff}
            fallback={<div class="changes-hint">Select a file to see its session diff.</div>}
          >
            {(diff) => <UnifiedDiff diff={diff()} />}
          </Show>
        </div>
      </div>
    </div>
  );
}
