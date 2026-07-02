// In-place block editing for the Notion SourceView — the UI half of the write path (docs/specs/notion-writes.md).
// Click or Enter on a `.wv-editable` block swaps it for a textarea holding the block's markdown source
// (notion-edit.blockSource); committing diffs the draft against the VERBATIM fetched markdown (buildUpdateOp)
// and posts `source-save-edit` — the refreshed `source-doc` re-render closes the editor — and Escape cancels.
// One block at a time. An edit is bound to the exact markdown it opened against: a re-render from the same
// string (theme switch) re-mounts the editor with its draft; a different string closes it.

import { openTarget, saveSourceEdit } from "../../bridge";
import { setContext } from "../../commands/context";
import { formatKey } from "../../commands/keybindings";
import { findCommand } from "../../commands/registry";
import { CommandIds } from "../../commands/types";
import { blockSource, buildUpdateOp } from "./notion-edit";

interface EditState {
  line: number;
  draft: string;
  // The display text at open: an unchanged draft cancels on blur (a changed one stays — never a silent discard).
  original: string;
  saving: boolean;
  error?: { message: string; stale: boolean } | undefined;
}

// The controller mounted by the active SourceView; App.tsx routes commands + source-edit-error through it.
let active: SourceEditController | undefined;

/** The active SourceView's edit controller, or undefined when no source tab is showing. */
export function activeSourceEditor(): SourceEditController | undefined {
  return active;
}

/** Drives the in-place block editor inside one SourceView's shadow root (one instance per mounted view). */
export class SourceEditController {
  private target = "";
  private markdown = "";
  private content: HTMLElement | undefined;
  private edit: EditState | undefined;
  private focusedBlock: HTMLElement | undefined;
  private textarea: HTMLTextAreaElement | undefined;
  private hint: HTMLElement | undefined;
  private box: HTMLElement | undefined;
  private restore: (() => void) | undefined;

  /**
   * Adopts a freshly rendered doc: decorates the editable blocks (tab stop + shortcut tooltip), re-mounts an
   * in-progress edit when the markdown is the same string it opened against, and closes it when it isn't —
   * including the refresh a successful save pushes, where focus returns to the edited block.
   */
  attach(content: HTMLElement, target: string, markdown: string): void {
    active = this;
    const sameDoc = target === this.target && markdown === this.markdown;
    // The saved-edit refresh: same page, new content, while our save was resolving (same-target guard, or a
    // different page arriving mid-save would grab focus on an unrelated block that shares the line index).
    const savedLine =
      target === this.target && !sameDoc && this.edit?.saving === true ? this.edit.line : undefined;
    if (!sameDoc) {
      this.closeState();
    }
    this.target = target;
    this.markdown = markdown;
    this.content = content;
    this.focusedBlock = undefined;
    const editKeys = findCommand(CommandIds.sourceEditBlock)?.keys ?? [];
    for (const el of content.querySelectorAll<HTMLElement>(".wv-editable")) {
      el.tabIndex = 0;
      el.title =
        editKeys.length > 0 ? `Edit block (${formatKey(editKeys[0] ?? "")})` : "Edit block";
    }
    content.addEventListener("focusin", (event) => {
      const el =
        event.target instanceof HTMLElement
          ? event.target.closest<HTMLElement>(".wv-editable")
          : null;
      this.focusedBlock = el ?? undefined;
      setContext("sourceBlockFocused", el !== null);
    });
    // Focus leaving the blocks entirely (another pane, the editor textarea) must drop the context key, or the
    // Edit Block chord would keep firing wherever the user types next.
    content.addEventListener("focusout", (event) => {
      const to =
        event.relatedTarget instanceof HTMLElement
          ? event.relatedTarget.closest(".wv-editable")
          : null;
      if (to === null) {
        this.focusedBlock = undefined;
        setContext("sourceBlockFocused", false);
      }
    });
    if (this.edit !== undefined) {
      const el = this.blockAt(this.edit.line);
      if (el !== undefined) {
        this.mount(el);
      } else {
        this.closeState(); // the block vanished from the render — nothing to anchor the editor to
      }
    } else if (savedLine !== undefined) {
      this.blockAt(savedLine)?.focus(); // the save's refresh landed: keep the keyboard on the edited block
    }
  }

  /** Forgets any in-progress edit (a non-markdown doc or an unmount took the view). */
  reset(): void {
    this.closeState();
    this.focusedBlock = undefined;
    if (active === this) {
      active = undefined;
    }
    setContext("sourceBlockFocused", false);
  }

  /** True when the click landed inside the open editor box — the textarea owns it, nothing else should react. */
  ownsClick(path: EventTarget[]): boolean {
    return path.some((n) => n instanceof HTMLElement && n.classList.contains("wv-editor-box"));
  }

  /** Opens the editor on a clicked `.wv-editable` block; false when the click hit nothing editable. */
  handleClick(path: EventTarget[]): boolean {
    const block = path.find(
      (n): n is HTMLElement => n instanceof HTMLElement && n.classList.contains("wv-editable"),
    );
    if (block === undefined) {
      return false;
    }
    this.open(block);
    return true;
  }

  /** Opens the editor on the focused block (the Edit Block command); false when there's nothing to edit. */
  editFocusedBlock(): boolean {
    if (this.focusedBlock === undefined || this.edit !== undefined) {
      return false;
    }
    this.open(this.focusedBlock);
    return true;
  }

  /**
   * Saves the draft (the Save Block Edit command). Declines — letting the key fall through — unless the editor
   * textarea owns focus: a changed draft stays open while the user works elsewhere, and plain Enter in the
   * terminal or palette must never fire a write to Notion.
   */
  commit(): boolean {
    const edit = this.edit;
    if (edit === undefined || this.textarea === undefined || edit.saving || !this.editorFocused()) {
      return false;
    }
    edit.draft = this.textarea.value;
    if (edit.draft === edit.original) {
      this.closeAndRefocus(edit.line);
      return true;
    }
    const op = buildUpdateOp(this.markdown, edit.line, edit.draft);
    if (!op.ok) {
      this.showError({ message: op.reason, stale: false });
      return true;
    }
    edit.saving = true;
    edit.error = undefined;
    this.textarea.disabled = true;
    this.box?.classList.add("wv-saving");
    if (this.hint !== undefined) {
      this.hint.textContent = "Saving…";
    }
    saveSourceEdit(this.target, op.oldStr, op.newStr);
    return true;
  }

  /**
   * Closes the editor and restores the block (the Cancel Block Edit command). Declines unless the editor
   * textarea owns focus, for the same reason as {@link commit} — Escape elsewhere is not the editor's.
   */
  cancel(): boolean {
    if (this.edit === undefined || this.edit.saving || !this.editorFocused()) {
      return false;
    }
    this.closeAndRefocus(this.edit.line);
    return true;
  }

  /**
   * A `source-edit-error` from the host: surfaces it inline at the edited block (stale offers a re-fetch).
   * False when this controller isn't showing that edit any more (the caller toasts it instead — a failed
   * write must reach the user wherever they are, never vanish).
   */
  showSaveError(target: string, message: string, stale: boolean): boolean {
    if (target !== this.target || this.edit === undefined) {
      return false;
    }
    this.edit.saving = false;
    this.box?.classList.remove("wv-saving");
    if (this.textarea !== undefined) {
      this.textarea.disabled = false;
      this.textarea.focus();
    }
    if (this.hint !== undefined) {
      this.hint.textContent = hintText();
    }
    this.showError({ message, stale });
    return true;
  }

  private open(el: HTMLElement): void {
    if (this.edit !== undefined) {
      return; // one block at a time — an unchanged editor cancels on blur before the click lands here
    }
    const line = Number(el.getAttribute("data-wv-line"));
    if (!Number.isInteger(line) || line < 0) {
      return;
    }
    const display = blockSource(this.markdown, line).display;
    this.edit = { line, draft: display, original: display, saving: false };
    setContext("sourceEditing", true);
    this.mount(el);
  }

  // Builds the editor DOM for the current edit state and swaps it in at `el` (a list item keeps its nested
  // list visible — only the item's own inline content is stashed).
  private mount(el: HTMLElement): void {
    const edit = this.edit;
    if (edit === undefined) {
      return;
    }
    const doc = el.ownerDocument;
    const box = doc.createElement("div");
    box.className = edit.saving ? "wv-editor-box wv-saving" : "wv-editor-box";
    const textarea = doc.createElement("textarea");
    textarea.className = "wv-block-editor";
    textarea.value = edit.draft;
    textarea.rows = 1;
    textarea.disabled = edit.saving;
    textarea.addEventListener("input", () => {
      edit.draft = textarea.value;
      autoGrow(textarea);
    });
    // An untouched editor closes when focus leaves it; a changed draft stays put (never a silent discard).
    // closeState (not closeAndRefocus): focus left deliberately — don't yank it back from where it went.
    textarea.addEventListener("focusout", () => {
      if (this.edit === edit && !edit.saving && textarea.value === edit.original) {
        this.closeState();
      }
    });
    const hint = doc.createElement("div");
    hint.className = "wv-edit-hint";
    hint.textContent = edit.saving ? "Saving…" : hintText();
    box.append(textarea, hint);
    this.textarea = textarea;
    this.hint = hint;
    this.box = box;

    if (el.tagName === "LI") {
      const inline = [...el.childNodes].filter(
        (n) => !(n instanceof HTMLElement && n.classList.contains("wv-children")),
      );
      for (const node of inline) {
        node.remove();
      }
      el.prepend(box);
      this.restore = (): void => {
        box.remove();
        el.prepend(...inline);
      };
    } else {
      el.style.display = "none";
      el.after(box);
      this.restore = (): void => {
        box.remove();
        el.style.display = "";
      };
    }
    if (edit.error !== undefined) {
      this.showError(edit.error);
    }
    textarea.focus();
    autoGrow(textarea);
    textarea.setSelectionRange(textarea.value.length, textarea.value.length);
  }

  // Renders (or replaces) the inline error row under the textarea; stale adds the re-fetch escape hatch.
  private showError(error: { message: string; stale: boolean }): void {
    const box = this.box;
    const edit = this.edit;
    if (box === undefined || edit === undefined) {
      return;
    }
    edit.error = error;
    box.querySelector(".wv-edit-error")?.remove();
    const row = box.ownerDocument.createElement("div");
    row.className = "wv-edit-error";
    row.append(box.ownerDocument.createTextNode(error.message));
    if (error.stale) {
      const refetch = box.ownerDocument.createElement("button");
      refetch.type = "button";
      refetch.className = "wv-edit-refetch";
      refetch.textContent = "Re-fetch page (discards this edit)";
      refetch.addEventListener("click", () => {
        this.closeState();
        openTarget(this.target);
      });
      row.append(refetch);
    }
    box.append(row);
  }

  // True when the editor textarea is the shadow root's focused element — the guard that keeps the plain
  // Enter/Escape chords scoped to the editor instead of hijacking them app-wide while a draft sits open.
  private editorFocused(): boolean {
    const root = this.textarea?.getRootNode();
    return root instanceof ShadowRoot && root.activeElement === this.textarea;
  }

  private closeAndRefocus(line: number): void {
    this.closeState();
    this.blockAt(line)?.focus();
  }

  // Tears down the editor DOM (restoring the block) and clears the edit state + context key.
  private closeState(): void {
    this.restore?.();
    this.restore = undefined;
    this.textarea = undefined;
    this.hint = undefined;
    this.box = undefined;
    this.edit = undefined;
    setContext("sourceEditing", false);
  }

  private blockAt(line: number): HTMLElement | undefined {
    return this.content?.querySelector<HTMLElement>(`[data-wv-line="${line}"]`) ?? undefined;
  }
}

// "⏎ Save · Esc Cancel" from the live command catalog — the keys are user-rebindable, never hardcoded.
function hintText(): string {
  const part = (id: string, label: string): string => {
    const keys = findCommand(id)?.keys ?? [];
    return keys.length > 0 ? `${formatKey(keys[0] ?? "")} ${label}` : label;
  };
  return `${part(CommandIds.sourceCommitEdit, "Save")} · ${part(CommandIds.sourceCancelEdit, "Cancel")}`;
}

function autoGrow(textarea: HTMLTextAreaElement): void {
  textarea.style.height = "auto";
  textarea.style.height = `${textarea.scrollHeight}px`;
}
