import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import { type AgentPaneUpdate, type AgentSlashEntry, postToBackend } from "../bridge";
import { readClipboardImage, readClipboardText } from "../clipboard-read";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { liveKeyLabel } from "../commands/keys-live";
import { dispatchCommand, registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { sendPastedImage, sendPastedImagesFromClipboard } from "../terminal/paste-image";
import { AgentSlashMenu } from "./AgentSlashMenu";
import {
  agentControlState,
  openControlPicker,
  setAgentControl,
  toggleAgentControl,
} from "./agent-controls-store";
import {
  captureAgentImagePaste,
  composerState,
  removeComposerAttachment,
  setComposerDraft,
  stageSkill,
  submitAgentTurn,
  unstageSkill,
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
import {
  activeTurnStartedAt,
  formatElapsed,
  hasActiveTurn,
  pendingApproval,
  pendingRequest,
} from "./turn-progress";

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
  const turnActive = createMemo(() => hasActiveTurn(props.messages));
  const pending = createMemo(() => (turnActive() ? pendingRequest(props.messages) : null));
  const pendingKind = createMemo(() => pending()?.kind ?? null);
  const canInterrupt = createMemo(() => props.slot !== null && turnActive());

  // Gates the Alt+Y / Alt+Shift+Y / Alt+N approval chords to moments a card is actually pending.
  createEffect(() => setContext("agentApprovalPending", pendingKind() === "approval"));
  onCleanup(() => setContext("agentApprovalPending", false));

  // Elapsed working time, anchored to when the running turn actually began (from the message stream) so it
  // reflects real duration and never restarts when switching away and back to a session. Ticks once a second.
  const turnStartedAt = createMemo(() => activeTurnStartedAt(props.messages));
  const [now, setNow] = createSignal(Date.now());
  createEffect(() => {
    if (turnStartedAt() === null) {
      return;
    }
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    onCleanup(() => clearInterval(timer));
  });
  const elapsed = (): string => {
    const started = turnStartedAt();
    return started === null ? "" : formatElapsed(now() - started);
  };

  const interruptKey = (): string => liveKeyLabel(CommandIds.agentInterrupt);
  const canSubmit = createMemo(() => {
    const state = composer();
    if (props.inputProtocol < 2) {
      return props.slot !== null && (state.draft.trim().length > 0 || pendingLegacyImages() > 0);
    }
    return (
      props.slot !== null &&
      state.submittingId === null &&
      state.attachments.every((attachment) => attachment.status === "ready") &&
      (state.draft.trim().length > 0 || state.attachments.length > 0 || state.skills.length > 0)
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
    } else if (entry.skillName !== null) {
      // Stage the skill so it submits as a structured skill input; clear the "/query" it replaces.
      stageSkill(props.backendId, slot, entry.skillName);
      setComposerDraft(props.backendId, slot, "");
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

  // A decision command answers the same approval the card chips advertise (turn-progress.pendingApproval).
  const registerDecision = (commandId: string, decision: string): (() => void) =>
    registerCommand(commandId, () => {
      const slot = props.slot;
      const request = pendingApproval(props.messages);
      if (slot === null || request === null) {
        return false;
      }
      postToBackend(props.backendId, {
        type: "agent-approval",
        slot,
        requestId: request.requestId,
        decision,
      });
      return true;
    });

  const offSubmit = registerCommand(CommandIds.agentSubmit, submit);
  const offInterrupt = registerCommand(CommandIds.agentInterrupt, interrupt);
  const offSelectModel = registerSelect(CommandIds.selectModel, "model");
  const offSelectApproval = registerSelect(CommandIds.selectApprovalPolicy, "approvalPolicy");
  const offSelectSandbox = registerSelect(CommandIds.selectSandbox, "sandbox");
  const offSelectEffort = registerSelect(CommandIds.selectEffort, "effort");
  // Fast Mode is a one-click toggle, not a picker: flip the serviceTier axis between its off and on option.
  const offToggleFast = registerCommand(CommandIds.toggleFastMode, () => {
    const slot = props.slot;
    if (slot === null) {
      return false;
    }
    const axis = agentControlState(slot).axes.find((candidate) => candidate.id === "serviceTier");
    if (axis === undefined) {
      return false;
    }
    toggleAgentControl(props.backendId, slot, axis);
    return true;
  });
  const offApprove = registerDecision(CommandIds.agentApprove, "accept");
  const offApproveForSession = registerDecision(
    CommandIds.agentApproveForSession,
    "acceptForSession",
  );
  const offDecline = registerDecision(CommandIds.agentDecline, "decline");
  onCleanup(offNativePaste);
  onCleanup(offSubmit);
  onCleanup(offInterrupt);
  onCleanup(offSelectModel);
  onCleanup(offSelectApproval);
  onCleanup(offSelectSandbox);
  onCleanup(offSelectEffort);
  onCleanup(offToggleFast);
  onCleanup(offApprove);
  onCleanup(offApproveForSession);
  onCleanup(offDecline);

  return (
    <form
      class="agent-compose"
      data-agent-composer
      onSubmit={(event) => {
        event.preventDefault();
        void dispatchCommand(CommandIds.agentSubmit);
      }}
    >
      <Show when={turnActive()}>
        <div class="agent-working" classList={{ waiting: pendingKind() !== null }}>
          <span class="agent-working-spinner" aria-hidden="true" />
          {/* Only the label is a live region — the ticking time would re-announce every second. */}
          <span class="agent-working-label" role="status">
            {workingLabel(pendingKind())}
          </span>
          <span class="agent-working-time">{elapsed()}</span>
          <Show when={interruptKey() !== ""}>
            <span class="agent-working-hint">{interruptKey()} to interrupt</span>
          </Show>
        </div>
      </Show>
      <Show when={composer().attachments.length > 0}>
        <div class="agent-attachments">
          <For each={composer().attachments}>
            {(attachment) => (
              <div
                class="agent-attachment"
                classList={{ failed: attachment.status === "failed" }}
                title={attachment.error ?? attachmentLabel(attachment.status)}
              >
                <img src={attachment.previewUrl} alt="Pasted attachment" />
                <Show when={attachment.status !== "ready"}>
                  <span>{attachmentLabel(attachment.status)}</span>
                </Show>
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
      <Show when={composer().skills.length > 0}>
        <div class="agent-skills">
          <For each={composer().skills}>
            {(skill) => (
              <span class="agent-skill-chip">
                /{skill}
                <button
                  type="button"
                  title="Remove skill"
                  onClick={() => {
                    const slot = props.slot;
                    if (slot !== null) {
                      unstageSkill(props.backendId, slot, skill);
                    }
                  }}
                >
                  ×
                </button>
              </span>
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
        placeholder={
          turnActive() ? "Steer the running turn…" : "Write a prompt — / for commands and skills"
        }
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
        <Show when={canInterrupt()}>
          <button
            type="button"
            title={`Interrupt the running turn${keyHint(CommandIds.agentInterrupt)}`}
            onClick={() => void dispatchCommand(CommandIds.agentInterrupt)}
          >
            Interrupt
          </button>
        </Show>
        <button
          type="submit"
          title={`${turnActive() ? "Steer the running turn" : "Run prompt"}${keyHint(CommandIds.agentSubmit)}`}
          disabled={!canSubmit()}
        >
          {composer().submittingId !== null ? "Sending…" : turnActive() ? "Steer" : "Run"}
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

function attachmentLabel(status: "reading" | "transferring" | "ready" | "failed"): string {
  switch (status) {
    case "reading":
      return "reading…";
    case "transferring":
      return "uploading…";
    case "failed":
      return "failed";
    default:
      return "ready";
  }
}

function workingLabel(pending: "approval" | "input" | null): string {
  if (pending === "approval") {
    return "Waiting on your approval";
  }
  return pending === "input" ? "Waiting on your answer" : "Working";
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
