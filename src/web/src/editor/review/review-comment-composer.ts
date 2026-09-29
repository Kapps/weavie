import { createEffect, createRoot, createSignal } from "solid-js";
import type { ReviewCommentDraft, ReviewCommentDrafts } from "./review-comment-drafts";

/** A disposable textarea view; only its exact session owns the text and pending submission. */
export function createReviewCommentComposer(
  drafts: ReviewCommentDrafts,
  draft: ReviewCommentDraft,
) {
  const element = document.createElement("div");
  element.className = "weavie-pr-composer";
  const input = document.createElement("textarea");
  input.className = "weavie-pr-composer-input";
  input.placeholder = draft.target.kind === "new" ? "Add a comment…" : "Reply…";
  input.rows = 2;
  input.value = draft.state().body;
  const button = document.createElement("button");
  button.className = "weavie-pr-composer-submit";
  button.type = "button";
  button.textContent = draft.target.kind === "new" ? "Comment" : "Reply";
  const status = document.createElement("div");
  status.className = "weavie-pr-composer-status";
  status.setAttribute("role", "status");
  element.append(input, button, status);
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "weavie-pr-composer-cancel";
  cancel.textContent = "Cancel";
  if (draft.target.kind === "new") element.append(cancel);
  const events = new AbortController();
  const eventOptions = { signal: events.signal };
  const [focused, setFocused] = createSignal(false);
  let endComposition: (() => void) | undefined;
  let disposed = false;
  const write = (): void => {
    if (!disposed && drafts.contains(draft)) drafts.write(draft, input.value);
  };
  input.addEventListener("input", write, eventOptions);
  input.addEventListener("focus", () => setFocused(true), eventOptions);
  input.addEventListener("blur", () => setFocused(false), eventOptions);
  input.addEventListener(
    "compositionstart",
    () => {
      endComposition?.();
      endComposition = drafts.beginComposition(draft);
    },
    eventOptions,
  );
  input.addEventListener(
    "compositionend",
    () => {
      write();
      endComposition?.();
      endComposition = undefined;
    },
    eventOptions,
  );
  const submit = (): void => {
    write();
    void drafts.submit(draft);
  };
  input.addEventListener(
    "keydown",
    (event) => {
      if (!event.isComposing && event.key === "Enter" && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        submit();
      }
    },
    eventOptions,
  );
  button.addEventListener("click", submit, eventOptions);
  cancel.addEventListener("click", () => drafts.discard(draft), eventOptions);
  const disposeEffect = createRoot((dispose) => {
    createEffect(() => {
      const state = draft.state();
      if (state.composing === 0 && input.value !== state.body) input.value = state.body;
      button.disabled =
        state.pending ||
        state.composing > 0 ||
        state.body.trim() === "" ||
        state.unavailable !== "";
      cancel.disabled = state.pending || state.composing > 0;
      status.textContent =
        state.error || state.unavailable || (state.pending ? "Posting comment…" : state.message);
      status.hidden = status.textContent === "";
    });
    return dispose;
  });
  return {
    element,
    input,
    focused,
    dispose: () => {
      if (disposed) return;
      disposed = true;
      events.abort();
      disposeEffect();
      endComposition?.();
      setFocused(false);
      element.remove();
    },
  };
}
