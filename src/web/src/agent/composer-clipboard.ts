import type { ClientSession } from "../bridge";
import { readClipboardContent } from "../clipboard-read";
import { registerCapturedCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";

interface ComposerPasteTarget {
  session: ClientSession;
  draft: () => string;
  setDraft: (draft: string) => void;
  pasteImage: (mime: string, dataB64: string) => void;
}

const targets = new Map<HTMLTextAreaElement, () => ComposerPasteTarget | null>();

export function registerComposerPasteTarget(
  textarea: HTMLTextAreaElement,
  target: () => ComposerPasteTarget | null,
): () => void {
  targets.set(textarea, target);
  return () => targets.delete(textarea);
}

export function installComposerClipboardCommand(): () => void {
  return registerCapturedCommand(CommandIds.agentPaste, () => {
    const textarea = document.activeElement as HTMLTextAreaElement;
    const target = targets.get(textarea)?.();
    if (target == null) return () => false;
    const start = textarea.selectionStart;
    const end = textarea.selectionEnd;
    return async () => {
      try {
        const content = await readClipboardContent();
        if (target.session.closed) return;
        if (content.kind === "image") {
          target.pasteImage(content.mime, content.dataB64);
        } else if (content.kind === "text") {
          const current = target.draft();
          const draft = current.slice(0, start) + content.text + current.slice(end);
          target.setDraft(draft);
          queueMicrotask(() => {
            if (document.activeElement === textarea && textarea.value === draft) {
              textarea.setSelectionRange(start + content.text.length, start + content.text.length);
            }
          });
        }
      } catch (error) {
        notify(
          "warn",
          `Couldn't paste from the clipboard: ${error instanceof Error ? error.message : String(error)}`,
        );
      }
    };
  });
}
