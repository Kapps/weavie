import { CommandIds } from "../../commands/types";
import type {
  InlineDiffOptions,
  ParkedReview,
  ReviewHistoryState,
  ReviewScopeState,
} from "../inline-diff";
import { fileIsKept } from "./review-sources";
import { createParkedToolbar, makeButton, withShortcut } from "./review-toolbar";
import type { ReviewToolbarCommands, ReviewToolbarPaint } from "./review-toolbar-state";

type ToolbarViewPaint =
  | Exclude<ReviewToolbarPaint, { status: "pending" }>
  | { status: "parked"; summary: ParkedReview };

export interface ReviewToolbarView {
  bar: HTMLElement;
  refresh(): void;
  menuContains(path: EventTarget[]): boolean;
}

const MAX_CHANGE_DOTS = 7;
const STALE_TITLE = "Waiting for the edited file's change geometry";

/** DOM-only controls; the surface presenter owns commands, readiness and lifetime. */
export function createReviewToolbarView(state: {
  paint: ToolbarViewPaint;
  scope: ReviewScopeState;
  menu: { open: boolean };
  commands: ReviewToolbarCommands;
  setScope(scope: ReviewScopeState["current"]): void;
  index(): number;
  stale(): boolean;
  history(): ReviewHistoryState;
}): ReviewToolbarView {
  const {
    nextChange,
    prevChange,
    nextFile,
    prevFile,
    accept,
    reject,
    comment,
    undoLast,
    redoReview,
  } = state.commands;
  const currentOptions = state.paint.status === "parked" ? undefined : state.paint.options;
  const currentHunks = state.paint.status === "ready" ? state.paint.markers.hunks : [];
  let counterNode: HTMLElement | undefined;
  let dotsNode: HTMLElement | undefined;
  let scopeMenuNode: HTMLElement | undefined;
  let scopeWrapNode: HTMLElement | undefined;
  let undoButton: HTMLButtonElement | undefined;
  let redoButton: HTMLButtonElement | undefined;
  const scopeActions: { button: HTMLButtonElement; title: string }[] = [];
  const trackScopeAction = (button: HTMLButtonElement): HTMLButtonElement => {
    scopeActions.push({ button, title: button.title });
    return button;
  };
  const syncHistoryButtons = (): void => {
    const history = state.history();
    if (undoButton !== undefined && undoButton.disabled === history.canUndo)
      undoButton.disabled = !history.canUndo;
    if (redoButton !== undefined && redoButton.disabled === history.canRedo)
      redoButton.disabled = !history.canRedo;
  };
  const scopeName = (scope: ReviewScopeState["current"]): string =>
    scope === "change" ? "Change" : scope === "file" ? "File" : "All";

  const renderCounter = (): void => {
    const options = currentOptions;
    if (counterNode === undefined || options === undefined) {
      return;
    }
    const total = currentHunks.length;
    const idx = state.index();
    const labelPart = options.reviewLabel === undefined ? "" : `${options.reviewLabel} · `;
    const filePart =
      options.fileCount !== undefined && options.fileCount > 1 && options.fileIndex !== undefined
        ? `file ${options.fileIndex}/${options.fileCount} · `
        : "";
    const text = `${labelPart}${filePart}change ${idx < 0 ? 0 : idx + 1}/${total}`;
    if (counterNode.textContent === text) return;
    counterNode.textContent = text;
    if (dotsNode === undefined) {
      return;
    }
    dotsNode.replaceChildren();
    if (total > 1 && total <= MAX_CHANGE_DOTS) {
      for (let i = 0; i < total; i++) {
        const dot = document.createElement("i");
        dot.className = i === idx ? "on" : i < idx ? "done" : "";
        dotsNode.appendChild(dot);
      }
    }
  };

  const navButtons = (): HTMLElement[] => [
    makeButton(
      "weavie-inline-nav",
      "↑",
      withShortcut("Previous change", CommandIds.prevChange),
      prevChange,
    ),
    makeButton(
      "weavie-inline-nav",
      "↓",
      withShortcut("Next change", CommandIds.nextChange),
      nextChange,
    ),
  ];

  const buildScopePicker = (options: InlineDiffOptions): HTMLElement => {
    const scope = state.scope.current;
    const wrap = document.createElement("div");
    wrap.className = "weavie-inline-scope";
    scopeWrapNode = wrap;
    const menu = document.createElement("div");
    menu.className = "weavie-inline-scope-menu";
    menu.style.display = state.menu.open ? "flex" : "none";
    scopeMenuNode = menu;
    const toggle = makeButton(
      "weavie-inline-scope-btn",
      `Scope: ${scopeName(scope)} ▾`,
      "Choose what Keep / Revert act on",
      () => {
        state.menu.open = !state.menu.open;
        menu.style.display = state.menu.open ? "flex" : "none";
      },
    );
    const head = document.createElement("div");
    head.className = "weavie-inline-scope-head";
    head.textContent = "Keep / Revert…";
    menu.appendChild(head);
    const addItem = (
      value: ReviewScopeState["current"],
      label: string,
      count: number | undefined,
    ): void => {
      const item = makeButton(
        `weavie-inline-scope-item${value === scope ? " active" : ""}`,
        label,
        label,
        () => {
          state.menu.open = false;
          state.setScope(value);
        },
      );
      if (count !== undefined) {
        const tag = document.createElement("span");
        tag.className = "weavie-inline-scope-count";
        tag.textContent = String(count);
        item.appendChild(tag);
      }
      menu.appendChild(item);
    };
    addItem("change", "This change", undefined);
    addItem("file", "This file", currentHunks.length);
    const manyFiles = (options.fileCount ?? 1) > 1;
    if (options.allActionsDisabled !== true) {
      addItem(
        "all",
        manyFiles ? "All files" : "All changes",
        manyFiles ? options.fileCount : currentHunks.length,
      );
    }
    wrap.append(toggle, menu);
    return wrap;
  };

  const buildAppliedBar = (bar: HTMLElement, options: InlineDiffOptions): void => {
    const multiFile =
      options.fileCount !== undefined &&
      options.fileCount > 1 &&
      options.onPrevFile !== undefined &&
      options.onNextFile !== undefined;
    if (multiFile) {
      bar.appendChild(
        makeButton(
          "weavie-inline-file",
          "←",
          withShortcut("Previous file", CommandIds.reviewPrevFile),
          prevFile,
        ),
      );
    }
    const stack = document.createElement("div");
    stack.className = "weavie-inline-stack";
    const name = document.createElement("span");
    name.className = "weavie-inline-stack-name";
    name.textContent = options.fileLabel ?? "";
    counterNode = document.createElement("span");
    counterNode.className = "weavie-inline-stack-sub";
    stack.append(name, counterNode);
    bar.appendChild(stack);
    if (multiFile) {
      bar.appendChild(
        makeButton(
          "weavie-inline-file",
          "→",
          withShortcut("Next file", CommandIds.reviewNextFile),
          nextFile,
        ),
      );
    }
    dotsNode = document.createElement("span");
    dotsNode.className = "weavie-inline-dots";
    bar.appendChild(dotsNode);
    bar.append(...navButtons());
    const divider = document.createElement("span");
    divider.className = "weavie-inline-divider";
    bar.appendChild(divider);
    bar.appendChild(buildScopePicker(options));

    const scope = state.scope.current;
    const allTarget = (options.fileCount ?? 1) > 1 ? "all files" : "all changes";
    const keepTip =
      scope === "change"
        ? "Keep this change"
        : scope === "file"
          ? "Keep this file"
          : `Keep ${allTarget}`;
    const revertTip =
      scope === "change"
        ? "Revert this change"
        : scope === "file"
          ? "Revert this file"
          : `Revert ${allTarget}`;
    const keep = makeButton(
      "weavie-inline-accept",
      "Keep",
      withShortcut(keepTip, CommandIds.acceptChange),
      accept,
    );
    const revert = makeButton(
      "weavie-inline-reject",
      "Revert",
      withShortcut(revertTip, CommandIds.rejectChange),
      reject,
    );
    if (scope === "change") {
      trackScopeAction(keep);
      trackScopeAction(revert);
    }
    bar.append(keep, revert);
    if (options.commenting !== undefined) {
      bar.appendChild(
        makeButton(
          "weavie-inline-comment",
          "Comment",
          withShortcut("Add a comment on the current line", CommandIds.reviewComment),
          comment,
        ),
      );
    }
    const histDivider = document.createElement("span");
    histDivider.className = "weavie-inline-divider";
    bar.appendChild(histDivider);
    undoButton = makeButton(
      "weavie-inline-hist",
      "↶",
      `Undo last review action — ${withShortcut("keep", CommandIds.undoKeep)}, ${withShortcut("revert", CommandIds.undoRevert)}`,
      undoLast,
    );
    redoButton = makeButton(
      "weavie-inline-hist",
      "↷",
      withShortcut("Redo review action", CommandIds.redoReview),
      redoReview,
    );
    bar.append(undoButton, redoButton);
    syncHistoryButtons();
    renderCounter();
  };

  const buildToolbar = (options: InlineDiffOptions): HTMLElement => {
    const bar = document.createElement("div");
    bar.className = "weavie-inline-toolbar";
    if (options.mode === "applied") {
      buildAppliedBar(bar, options);
      return bar;
    }
    bar.append(...navButtons());
    if (options.mode === "review") {
      if (options.onAccept !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-accept",
            "Keep",
            withShortcut("Keep this change", CommandIds.acceptChange),
            accept,
          ),
        );
      }
      if (options.onReject !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-reject",
            "Reject",
            withShortcut("Reject this change", CommandIds.rejectChange),
            reject,
          ),
        );
      }
    }
    return bar;
  };

  const unavailable = (
    paint: Extract<ReviewToolbarPaint, { status: "unavailable" }>,
  ): HTMLElement => {
    const { options, message } = paint;
    const kept = fileIsKept(options);
    const bar = document.createElement("div");
    bar.className = "weavie-inline-toolbar";
    const multiple =
      (options.fileCount ?? 1) > 1 &&
      options.onPrevFile !== undefined &&
      options.onNextFile !== undefined;
    if (multiple)
      bar.append(
        makeButton(
          "weavie-inline-file",
          "←",
          withShortcut("Previous file", CommandIds.reviewPrevFile),
          prevFile,
        ),
      );
    const warning = document.createElement("span");
    warning.className = "weavie-inline-stack-sub";
    warning.textContent = kept ? `File kept · ${message.toLowerCase()}` : message;
    bar.append(warning);
    if (multiple)
      bar.append(
        makeButton(
          "weavie-inline-file",
          "→",
          withShortcut("Next file", CommandIds.reviewNextFile),
          nextFile,
        ),
      );
    if ((options.mode === "applied" && !kept) || options.mode === "review") {
      const applied = options.mode === "applied";
      bar.append(
        makeButton(
          "weavie-inline-accept",
          applied ? "Keep file" : "Keep",
          withShortcut(applied ? "Keep this file" : "Keep this change", CommandIds.acceptChange),
          applied ? state.commands.keepFile : accept,
        ),
        makeButton(
          "weavie-inline-reject",
          applied ? "Revert file" : "Reject",
          withShortcut(
            applied ? "Revert this file" : "Reject this change",
            CommandIds.rejectChange,
          ),
          applied ? state.commands.revertFile : reject,
        ),
      );
    }
    return bar;
  };
  const create = (): HTMLElement => {
    const paint = state.paint;
    if (paint.status === "unavailable") return unavailable(paint);
    if (paint.status === "ready") return buildToolbar(paint.options);
    const controls = createParkedToolbar(
      paint.summary,
      { stepIn: nextChange, nextFile, prevFile, undo: undoLast, redo: redoReview },
      state.history(),
    );
    undoButton = controls.undo;
    redoButton = controls.redo;
    return controls.bar;
  };
  const bar = create();
  return {
    bar,
    menuContains: (path) => scopeWrapNode !== undefined && path.includes(scopeWrapNode),
    refresh: () => {
      renderCounter();
      syncHistoryButtons();
      const blocked = state.stale();
      for (const { button, title } of scopeActions) {
        if (button.disabled !== blocked) button.disabled = blocked;
        const currentTitle = blocked ? STALE_TITLE : title;
        if (button.title !== currentTitle) button.title = currentTitle;
      }
      const display = state.menu.open ? "flex" : "none";
      if (scopeMenuNode !== undefined && scopeMenuNode.style.display !== display)
        scopeMenuNode.style.display = display;
    },
  };
}
