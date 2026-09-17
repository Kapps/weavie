import { createSignal, type JSX } from "solid-js";
import { type ClientSession, requestDiffRefs } from "../bridge";
import { BranchTypeahead } from "./BranchTypeahead";
import { ModalShell, PromptActions, PromptButton } from "./ModalShell";

// Prompt for "Diff Against…": name the ref to review the working tree against — a typeahead over the active
// session's local and remote-tracking branches (main, origin/main), or any typed commit-ish (a tag, a SHA,
// HEAD~2). Enter diffs, Esc cancels.
export function DiffAgainstPrompt(props: {
  session: ClientSession;
  onPick: (ref: string) => void;
  onCancel: () => void;
}): JSX.Element {
  const [ref, setRef] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);

  const [error, setError] = createSignal("");
  const [defaultRef, setDefaultRef] = createSignal<string | null>(null);
  void requestDiffRefs(props.session)
    .then((result) => {
      setBranches(result.refs);
      setDefaultRef(result.defaultRef);
    })
    .catch((error: unknown) => setError(String(error)));

  const pick = (name: string): void => {
    const target = name.trim() || defaultRef();
    if (target !== null) {
      props.onPick(target);
    }
  };

  return (
    <ModalShell labelledBy="diff-against-title" onDismiss={props.onCancel} class="session-prompt">
      <div class="confirm-title" id="diff-against-title">
        Diff against
      </div>
      <div class="confirm-body">
        Review the working tree's changes against a branch, tag, or commit (from its merge-base with
        HEAD).
      </div>
      <div class="session-prompt-field">
        <BranchTypeahead
          idPrefix="diff-against"
          placeholder={defaultRef() ?? "branch, tag, or commit (e.g. origin/main, HEAD~2)"}
          ariaLabel="Ref to diff against"
          branches={branches()}
          value={ref()}
          setValue={setRef}
          onSubmit={(text) => pick(text)}
          onCancel={() => props.onCancel()}
        />
      </div>
      <div role="alert">{error()}</div>
      <PromptActions>
        <PromptButton label="Cancel" shortcut="Esc" title="Cancel (Esc)" onClick={props.onCancel} />
        <PromptButton
          label="Diff"
          shortcut="Enter"
          title={`Diff against ${ref().trim() || defaultRef() || "the typed ref"} (Enter)`}
          onClick={() => pick(ref().trim())}
          disabled={!ref().trim() && defaultRef() === null}
          primary
        />
      </PromptActions>
    </ModalShell>
  );
}
