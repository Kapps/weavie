import { createSignal } from "solid-js";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";
import type { EditorOptionsSpec } from "../../messaging/protocol-types";

type ReviewOptions = Pick<
  EditorOptionsSpec,
  "reviewComments" | "reviewHints" | "reviewStepSettled"
>;

const pick = (options: EditorOptionsSpec): ReviewOptions => ({
  reviewComments: options.reviewComments,
  reviewHints: options.reviewHints,
  reviewStepSettled: options.reviewStepSettled,
});

const [options, setOptions] = createSignal(pick(currentEditorOptions()), {
  equals: (a, b) =>
    a.reviewComments === b.reviewComments &&
    a.reviewHints === b.reviewHints &&
    a.reviewStepSettled === b.reviewStepSettled,
});
onEditorOptionsChanged((next) => setOptions(pick(next)));

/** Which review items show and are stepped through: the `editor.review*` settings. */
export const reviewOptions = options;
