import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { type AgentPaneUpdate, type AgentSlashEntry, postToBackend } from "../bridge";
import { readClipboardImage, readClipboardText } from "../clipboard-read";
import { keyHint } from "../commands/key-hint";
import { dispatchCommand, registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { sendPastedImage, sendPastedImagesFromClipboard } from "../terminal/paste-image";
import { AgentSlashMenu } from "./AgentSlashMenu";
import { agentControlState, openControlPicker, setAgentControl } from "./agent-controls-store";
import {
  captureAgentImagePaste,
  composerState,
  removeComposerAttachment,
  setComposerDraft,
  submitAgentTurn,
  uploadAgentImage,
} from "./composer-store";
import {
  caretOnFirstLine,
  caretOnLastLine,
  type HistoryCursor,
  type HistoryRecall,
  IDLE_CURSOR,
  recallNext,
  recallPrevious,
  submittedPrompts,
} from "./prompt-history";
import { filterSlash, slashQuery } from "./slash";

export function AgentComposer(props: {
  active: boolean;
  backendId: string;
  inputProtocol: number;
  messages: AgentPaneUpdate[];
  slot: string | null;
  onSubmitted: () => void;
}): JSX.Element {
  let textareaRef: HTMLTextAreaElement | undefined;
  const appliedDraftIndexes = new Map<string, number>();
  const composer = createMemo(() => composerState(props.backendId, props.slot));
  const pendingLegacyImages = createMemo(() => countPendingLegacyImages(props.messages));
  const canInterrupt = createMemo(() => props.slot !== null && hasActiveTurn(props.messages));
  const canSubmit = createMemo(() => {
    const state = composer();
    if (props.inputProtocol < 2) {
      return props.slot !== null && (state.draft.trim().length > 0 || pendingLegacyImages() > 0);
    }
    return (
      props.slot !== null &&
      state.submittingId === null &&
      state.attachments.every((attachment) => attachment.status === "ready") &&
      (state.draft.trim().length > 0 || state.attachments.length > 0)
    );
  });

  createEffect(() => applyPrefill(props, appliedDraftIndexes));

  const history = createMemo(() => submittedPrompts(props.messages));
  const [historyCursor, setHistoryCursor] = createSignal<HistoryCursor>(IDLE_CURSOR);
  // Switching sessions abandons any in-progress history browse.
  createEffect(() => {
    props.slot;
    setHistoryCursor(IDLE_CURSOR);
  });

  const [slashDismissed, setSlashDismissed] = createSignal(false);
  const slashText = createMemo(() => slashQuery(composer().draft));
  const slashEntries = createMemo(() => {
    const query = slashText();
    return query === null || slashDismissed()
      ? []
      : filterSlash(agentControlState(props.slot).slash, query);
  });
  // A draft that's no longer a slash command clears any prior dismissal, so the next "/" reopens the menu.
  createEffect(() => {
    if (slashText() === null) {
      setSlashDismissed(false);
    }
  });

  const acceptSlash = (entry: AgentSlashEntry): void => {
    const slot = props.slot;
    if (slot === null) {
      return;
    }
    if (entry.commandId !== null) {
      setComposerDraft(props.backendId, slot, "");
      void dispatchCommand(entry.commandId);
    } else if (entry.insertText !== null) {
      setComposerDraft(props.backendId, slot, entry.insertText);
      const caret = entry.insertText.length;
      requestAnimationFrame(() => textareaRef?.setSelectionRange(caret, caret));
    }
    setSlashDismissed(false);
    textareaRef?.focus();
  };

  const applyRecall = (recall: HistoryRecall | null): boolean => {
    const slot = props.slot;
    if (recall === null || slot === null) {
      return false;
    }
    setHistoryCursor(recall.next);
    setComposerDraft(props.backendId, slot, recall.text);
    const caret = recall.text.length;
    requestAnimationFrame(() => textareaRef?.setSelectionRange(caret, caret));
    return true;
  };

  // Shell-style history: Up recalls the previous prompt only with a collapsed caret on the first line, Down
  // the next only on the last line — otherwise the arrow moves the caret within a multi-line draft as usual.
  const onComposerKeyDown = (event: KeyboardEvent): void => {
    const element = textareaRef;
    if (
      element === undefined ||
      // While the slash menu is open its own handler owns Up/Down; don't also recall history.
      slashEntries().length > 0 ||
      event.shiftKey ||
      event.altKey ||
      event.ctrlKey ||
      event.metaKey ||
      element.selectionStart !== element.selectionEnd
    ) {
      return;
    }
    if (event.key === "ArrowUp" && caretOnFirstLine(element.value, element.selectionStart)) {
      if (applyRecall(recallPrevious(history(), historyCursor(), element.value))) {
        event.preventDefault();
      }
    } else if (event.key === "ArrowDown" && caretOnLastLine(element.value, element.selectionEnd)) {
      if (applyRecall(recallNext(history(), historyCursor()))) {
        event.preventDefault();
      }
    }
  };

  const offNativePaste = registerCommand(CommandIds.agentPaste, async () => {
    const slot = props.slot;
    if (slot === null) {
      return;
    }
    const backendId = props.backendId;
    const inputProtocol = props.inputProtocol;
    const selectionStart = textareaRef?.selectionStart;
    const selectionEnd = textareaRef?.selectionEnd;
    try {
      const image = await readClipboardImage();
      if (image.mime.length > 0) {
        if (inputProtocol >= 2) {
          uploadAgentImage(
            backendId,
            slot,
            image.mime,
            image.dataB64,
            `data:${image.mime};base64,${image.dataB64}`,
          );
        } else {
          sendPastedImage(backendId, slot, image.mime, image.dataB64);
        }
        return;
      }
      const text = await readClipboardText();
      if (text.length === 0) {
        return;
      }
      const current = composerState(backendId, slot).draft;
      const start = selectionStart ?? current.length;
      const end = selectionEnd ?? start;
      setComposerDraft(backendId, slot, current.slice(0, start) + text + current.slice(end));
      if (backendId === props.backendId && slot === props.slot) {
        requestAnimationFrame(() =>
          textareaRef?.setSelectionRange(start + text.length, start + text.length),
        );
      }
    } catch (error) {
      notify(
        "warn",
        `Couldn't paste from the clipboard: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  });

  const submit = (): boolean => {
    const slot = props.slot;
    if (!props.active || slot === null) {
      return false;
    }
    if (props.inputProtocol < 2) {
      const state = composerState(props.backendId, slot);
      if (state.draft.trim().length === 0 && pendingLegacyImages() === 0) {
        return false;
      }
      postToBackend(props.backendId, { type: "agent-submit", slot, prompt: state.draft.trim() });
      setComposerDraft(props.backendId, slot, "");
    } else if (!submitAgentTurn(props.backendId, slot)) {
      return false;
    }
    setHistoryCursor(IDLE_CURSOR);
    props.onSubmitted();
    return true;
  };

  const interrupt = (): boolean => {
    const slot = props.slot;
    if (!props.active || slot === null || !canInterrupt()) {
      return false;
    }
    postToBackend(props.backendId, { type: "agent-interrupt", slot });
    return true;
  };

  // A control command applies its `value` arg directly (palette / Claude), or opens the picker when bare.
  const registerSelect = (commandId: string, axis: string): (() => void) =>
    registerCommand(commandId, (args: unknown) => {
      const slot = props.slot;
      if (slot === null) {
        return false;
      }
      const value = (args as { value?: unknown } | undefined)?.value;
      if (typeof value === "string" && value.length > 0) {
        setAgentControl(props.backendId, slot, axis, value);
      } else {
        openControlPicker(axis);
      }
      return true;
    });

  const offSubmit = registerCommand(CommandIds.agentSubmit, submit);
  const offInterrupt = registerCommand(CommandIds.agentInterrupt, interrupt);
  const offSelectModel = registerSelect(CommandIds.selectModel, "model");
  const offSelectApproval = registerSelect(CommandIds.selectApprovalPolicy, "approvalPolicy");
  const offSelectSandbox = registerSelect(CommandIds.selectSandbox, "sandbox");
  onCleanup(offNativePaste);
  onCleanup(offSubmit);
  onCleanup(offInterrupt);
  onCleanup(offSelectModel);
  onCleanup(offSelectApproval);
  onCleanup(offSelectSandbox);

  return (
    <form
      class="agent-compose"
      data-agent-composer
      onSubmit={(event) => {
        event.preventDefault();
        void dispatchCommand(CommandIds.agentSubmit);
      }}
    >
      <Show when={composer().attachments.length > 0}>
        <div class="agent-attachments">
          <For each={composer().attachments}>
            {(attachment) => (
              <div
                class="agent-attachment"
                classList={{ failed: attachment.status === "failed" }}
                title={attachment.error ?? attachment.status}
              >
                <img src={attachment.previewUrl} alt="Pasted attachment" />
                <span>{attachment.status}</span>
                <button
                  type="button"
                  title="Remove attachment"
                  onClick={() => {
                    const slot = props.slot;
                    if (slot !== null) {
                      removeComposerAttachment(props.backendId, slot, attachment.id);
                    }
                  }}
                >
                  ×
                </button>
              </div>
            )}
          </For>
        </div>
      </Show>
      <AgentSlashMenu
        entries={slashEntries()}
        onAccept={acceptSlash}
        onDismiss={() => setSlashDismissed(true)}
      />
      <span class="agent-compose-prompt">prompt&gt;</span>
      <textarea
        ref={textareaRef}
        rows={1}
        value={composer().draft}
        placeholder="Write a prompt for Codex..."
        onKeyDown={onComposerKeyDown}
        onInput={(event) => {
          const slot = props.slot;
          if (slot !== null) {
            setComposerDraft(props.backendId, slot, event.currentTarget.value);
            // Editing starts a fresh draft, ending any history browse.
            if (historyCursor().cursor !== null) {
              setHistoryCursor(IDLE_CURSOR);
            }
          }
        }}
        onPaste={(event) => {
          const slot = props.slot;
          if (slot !== null) {
            if (props.inputProtocol >= 2) {
              captureAgentImagePaste(event, props.backendId, slot);
            } else {
              sendPastedImagesFromClipboard(event, props.backendId, slot);
            }
          }
        }}
      />
      <div class="agent-compose-actions">
        <button
          type="button"
          title={`Interrupt active Codex turn${keyHint(CommandIds.agentInterrupt)}`}
          disabled={!canInterrupt()}
          onClick={() => void dispatchCommand(CommandIds.agentInterrupt)}
        >
          Interrupt
        </button>
        <button
          type="submit"
          title={`Run prompt${keyHint(CommandIds.agentSubmit)}`}
          disabled={!canSubmit()}
        >
          {composer().submittingId === null ? "Run" : "Sending…"}
        </button>
      </div>
      <Show when={composer().error !== null}>
        <div class="agent-compose-error">{composer().error}</div>
      </Show>
    </form>
  );
}

function applyPrefill(
  props: { backendId: string; messages: AgentPaneUpdate[]; slot: string | null },
  appliedIndexes: Map<string, number>,
): void {
  const slot = props.slot;
  if (slot === null) {
    return;
  }
  const key = `${props.backendId}\u0000${slot}`;
  for (let index = props.messages.length - 1; index >= 0; index -= 1) {
    const message = props.messages[index];
    if (message?.type === "draft" && index !== appliedIndexes.get(key)) {
      appliedIndexes.set(key, index);
      setComposerDraft(props.backendId, slot, message.text ?? "");
      return;
    }
  }
}

function countPendingLegacyImages(messages: AgentPaneUpdate[]): number {
  return messages.reduce((count, message) => {
    if (message.type !== "user-image") {
      return count;
    }
    if (message.status === "attached") {
      return count + 1;
    }
    return message.status === "submitted" ? Math.max(0, count - 1) : count;
  }, 0);
}

function hasActiveTurn(messages: AgentPaneUpdate[]): boolean {
  let active = false;
  for (const message of messages) {
    if (message.type === "turn-started") {
      active = true;
    } else if (message.type === "turn-completed" || message.type === "turn-interrupted") {
      active = false;
    }
  }
  return active;
}
