import { type JSX, createSignal } from "solid-js";
import { Portal } from "solid-js/web";
import { activeBackendId, requestBranches } from "../bridge";
import { BranchTypeahead } from "./BranchTypeahead";

// Prompt for "Diff Against…": name the ref to review the working tree against — a typeahead over the active
// session's local branches, or any typed commit-ish (a tag, a SHA, HEAD~2). Enter diffs, Esc cancels.
export function DiffAgainstPrompt(props: {
  onPick: (ref: string) => void;
  onCancel: () => void;
}): JSX.Element {
  const [ref, setRef] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);

  void requestBranches(activeBackendId()).then(setBranches);

  const pick = (name: string): void => {
    if (name.length > 0) {
      props.onPick(name);
    }
  };

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onCancel()}>
        <div
          class="confirm-dialog session-prompt"
          role="dialog"
          aria-modal="true"
          aria-labelledby="diff-against-title"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title" id="diff-against-title">
            Diff against
          </div>
          <div class="confirm-body">
            Review the working tree's changes against a branch, tag, or commit (from its merge-base
            with HEAD).
          </div>
          <div class="session-prompt-field">
            <BranchTypeahead
              idPrefix="diff-against"
              placeholder="branch, tag, or commit (e.g. main, HEAD~2)"
              ariaLabel="Ref to diff against"
              branches={branches()}
              value={ref()}
              setValue={setRef}
              onSubmit={(text) => pick(text)}
              onCancel={() => props.onCancel()}
            />
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
              onClick={() => pick(ref().trim())}
              title={`Diff against ${ref().trim().length > 0 ? ref().trim() : "the typed ref"} (Enter)`}
            >
              <span class="session-prompt-btn-label">Diff</span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
