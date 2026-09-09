import { keyHint } from "../../commands/key-hint";
import { CommandIds } from "../../commands/types";
import type { ParkedReview, ReviewHistoryState } from "../inline-diff";

export const makeButton = (
  className: string,
  label: string,
  title: string,
  onClick: () => void,
): HTMLButtonElement => {
  const button = document.createElement("button");
  button.type = "button";
  button.className = className;
  button.textContent = label;
  button.title = title;
  button.addEventListener("click", () => onClick());
  return button;
};

// Every toolbar button advertises its shortcut on hover ("<label> (<shortcut>)") using the command's
// effective keys; unbound commands show just the label.
export const withShortcut = (label: string, commandId: string): string => {
  return label + keyHint(commandId);
};

export function createParkedToolbar(
  summary: Pick<ParkedReview, "fileCount" | "label">,
  actions: { stepIn(): void; nextFile(): void; prevFile(): void; undo(): void; redo(): void },
  history: ReviewHistoryState,
): { bar: HTMLElement; undo: HTMLButtonElement; redo: HTMLButtonElement } {
  const bar = document.createElement("div");
  bar.className = "weavie-inline-toolbar";
  const multiFile = summary.fileCount > 1;
  if (multiFile) {
    bar.appendChild(
      makeButton(
        "weavie-inline-file",
        "←",
        withShortcut("Previous file", CommandIds.reviewPrevFile),
        actions.prevFile,
      ),
    );
  }
  const stack = document.createElement("div");
  stack.className = "weavie-inline-stack";
  const name = document.createElement("span");
  name.className = "weavie-inline-stack-name";
  name.textContent = "Review changes";
  const sub = document.createElement("span");
  sub.className = "weavie-inline-stack-sub";
  const parkedLabel = summary.label === undefined ? "" : `${summary.label} · `;
  sub.textContent = `${parkedLabel}${summary.fileCount} file${summary.fileCount === 1 ? "" : "s"} · press ↓ to start`;
  stack.append(name, sub);
  bar.appendChild(stack);
  if (multiFile) {
    bar.appendChild(
      makeButton(
        "weavie-inline-file",
        "→",
        withShortcut("Next file", CommandIds.reviewNextFile),
        actions.nextFile,
      ),
    );
  }
  bar.append(
    makeButton(
      "weavie-inline-nav",
      "↑",
      withShortcut("Review changes", CommandIds.prevChange),
      actions.stepIn,
    ),
    makeButton(
      "weavie-inline-nav",
      "↓",
      withShortcut("Review changes", CommandIds.nextChange),
      actions.stepIn,
    ),
  );
  const divider = document.createElement("span");
  divider.className = "weavie-inline-divider";
  bar.appendChild(divider);
  // Inert until a change is in view, but shown so the bar reads as the same toolbar at "change 0".
  const keep = makeButton(
    "weavie-inline-accept",
    "Keep",
    withShortcut("Step into a change first", CommandIds.nextChange),
    () => {},
  );
  const revert = makeButton(
    "weavie-inline-reject",
    "Revert",
    withShortcut("Step into a change first", CommandIds.nextChange),
    () => {},
  );
  keep.disabled = true;
  revert.disabled = true;
  bar.append(keep, revert);
  const histDivider = document.createElement("span");
  histDivider.className = "weavie-inline-divider";
  bar.appendChild(histDivider);
  const undoButton = makeButton(
    "weavie-inline-hist",
    "↶",
    `Undo last review action — ${withShortcut("keep", CommandIds.undoKeep)}, ${withShortcut("revert", CommandIds.undoRevert)}`,
    actions.undo,
  );
  const redoButton = makeButton(
    "weavie-inline-hist",
    "↷",
    withShortcut("Redo review action", CommandIds.redoReview),
    actions.redo,
  );
  bar.append(undoButton, redoButton);
  undoButton.disabled = !history.canUndo;
  redoButton.disabled = !history.canRedo;
  return { bar, undo: undoButton, redo: redoButton };
}

/** Navigation at change zero is shared by file and multi-file review presentations. */
export function createParkedNavigation(summary: ParkedReview) {
  const run = (action: () => void): boolean => {
    action();
    return true;
  };
  return {
    nextChange: () => run(summary.stepIn),
    prevChange: () => run(summary.stepIn),
    accept: () => run(summary.stepIn),
    nextFile: () => run(summary.nextFile),
    prevFile: () => run(summary.prevFile),
  };
}
