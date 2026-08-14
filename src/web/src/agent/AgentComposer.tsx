import { createEffect, createMemo, createSignal, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentSlashEntry, ClientSession } from "../bridge";
import { readClipboardImage, readClipboardText } from "../clipboard-read";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { dispatchCommand, registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { sendPastedImage, sendPastedImagesFromClipboard } from "../terminal/paste-image";
import { AgentAttachmentStrip } from "./AgentAttachmentStrip";
import { AgentSlashMenu } from "./AgentSlashMenu";
import { AgentWorkingStatus } from "./AgentWorkingStatus";
import {
  agentControlState,
  currentModel,
  MODEL_AXIS,
  openControlPicker,
  setAgentControl,
  toggleAgentControl,
  toggleModelFast,
} from "./agent-controls-store";
import {
  type AgentPlanIdentity,
  planIdentityArgsSupplied,
  planIdentityFromArgs,
} from "./agent-plan";
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
  type HistoryCursor,
  type HistoryRecall,
  IDLE_CURSOR,
  recallNext,
  recallPrevious,
} from "./prompt-history";
import { filterSlash, slashQuery } from "./slash";
import { caretOnFirstVisualLine, caretOnLastVisualLine } from "./textarea-lines";
import type { PendingRequestKind } from "./turn-progress";

export function AgentComposer(props: {
  active: boolean;
  compact: boolean;
  history: readonly string[];
  inputProtocol: number;
  latestPlan: AgentPlanIdentity | null;
  pendingApprovalId: string | null;
  pendingKind: PendingRequestKind | null;
  pendingLegacyImageCount: number;
  session: ClientSession | null;
  turnActive: boolean;
  turnStartedAt: number | null;
  onSubmitted: () => void;
}): JSX.Element {
  let textareaRef: HTMLTextAreaElement | undefined;
  const composer = createMemo(() => composerState(props.session));
  const canInterrupt = createMemo(() => props.session !== null && props.turnActive);

  createEffect(() => setContext("agentApprovalPending", props.pendingKind === "approval"));
  onCleanup(() => setContext("agentApprovalPending", false));

  const canSubmit = createMemo(() => {
    const state = composer();
    if (props.inputProtocol < 2) {
      return (
        props.session !== null &&
        (state.draft.trim().length > 0 || props.pendingLegacyImageCount > 0)
      );
    }
    return (
      props.session !== null &&
      state.submittingId === null &&
      state.attachments.every((attachment) => attachment.status === "ready") &&
      (state.draft.trim().length > 0 || state.attachments.length > 0 || state.skills.length > 0)
    );
  });

  const [historyCursor, setHistoryCursor] = createSignal<HistoryCursor>(IDLE_CURSOR);
  // Switching sessions abandons any in-progress history browse.
  createEffect(() => {
    props.session;
    setHistoryCursor(IDLE_CURSOR);
  });

  const [slashDismissed, setSlashDismissed] = createSignal(false);
  const slashText = createMemo(() => slashQuery(composer().draft));
  const slashEntries = createMemo(() => {
    const query = slashText();
    return query === null || slashDismissed()
      ? []
      : filterSlash(agentControlState(props.session).slash, query);
  });
  // A draft that's no longer a slash command clears any prior dismissal, so the next "/" reopens the menu.
  createEffect(() => {
    if (slashText() === null) {
      setSlashDismissed(false);
    }
  });

  const placeCaretAfterDraftUpdate = (draft: string, caret: number): void => {
    queueMicrotask(() => {
      const element = textareaRef;
      if (element?.value === draft) {
        element.setSelectionRange(caret, caret);
      }
    });
  };

  const acceptSlash = (entry: AgentSlashEntry): void => {
    const session = props.session;
    if (session === null) {
      return;
    }
    if (entry.commandId !== null) {
      setComposerDraft(session, "");
      void runCommandWithFeedback(entry.commandId);
    } else if (entry.skillName !== null) {
      // Stage the skill so it submits as a structured skill input; clear the "/query" it replaces.
      stageSkill(session, entry.skillName);
      setComposerDraft(session, "");
    } else if (entry.insertText !== null) {
      setComposerDraft(session, entry.insertText);
      placeCaretAfterDraftUpdate(entry.insertText, entry.insertText.length);
    }
    setSlashDismissed(false);
    textareaRef?.focus();
  };

  const applyRecall = (recall: HistoryRecall | null): boolean => {
    const session = props.session;
    if (recall === null || session === null) {
      return false;
    }
    setHistoryCursor(recall.next);
    setComposerDraft(session, recall.text);
    placeCaretAfterDraftUpdate(recall.text, recall.text.length);
    return true;
  };

  // Shell-style history: Up recalls only from the first rendered line, Down only from the last — otherwise
  // the arrow moves the collapsed caret within a multi-line draft as usual.
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
    if (event.key === "ArrowUp" && caretOnFirstVisualLine(element)) {
      if (applyRecall(recallPrevious(props.history, historyCursor(), element.value))) {
        event.preventDefault();
      }
    } else if (event.key === "ArrowDown" && caretOnLastVisualLine(element)) {
      if (applyRecall(recallNext(props.history, historyCursor()))) {
        event.preventDefault();
      }
    }
  };

  const paste = async (): Promise<void> => {
    const session = props.session;
    if (session === null) {
      return;
    }
    const inputProtocol = props.inputProtocol;
    const selectionStart = textareaRef?.selectionStart;
    const selectionEnd = textareaRef?.selectionEnd;
    try {
      const image = await readClipboardImage();
      if (image.mime.length > 0) {
        if (inputProtocol >= 2) {
          uploadAgentImage(
            session,
            image.mime,
            image.dataB64,
            `data:${image.mime};base64,${image.dataB64}`,
          );
        } else {
          sendPastedImage(session, image.mime, image.dataB64);
        }
        return;
      }
      const text = await readClipboardText();
      if (text.length === 0) {
        return;
      }
      const current = composerState(session).draft;
      const start = selectionStart ?? current.length;
      const end = selectionEnd ?? start;
      const draft = current.slice(0, start) + text + current.slice(end);
      setComposerDraft(session, draft);
      if (props.session === session) {
        placeCaretAfterDraftUpdate(draft, start + text.length);
      }
    } catch (error) {
      notify(
        "warn",
        `Couldn't paste from the clipboard: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  };

  const submit = (): boolean => {
    const session = props.session;
    if (!props.active || session === null) {
      return false;
    }
    if (props.inputProtocol < 2) {
      const state = composerState(session);
      if (state.draft.trim().length === 0 && props.pendingLegacyImageCount === 0) {
        return false;
      }
      session.feature("agent").publish("submit", {
        id: "",
        prompt: state.draft.trim(),
        attachmentIds: [],
        skills: [],
      });
      setComposerDraft(session, "");
    } else if (!submitAgentTurn(session)) {
      return false;
    }
    setHistoryCursor(IDLE_CURSOR);
    props.onSubmitted();
    return true;
  };

  const interrupt = (): boolean => {
    const session = props.session;
    if (!props.active || session === null || !canInterrupt()) {
      return false;
    }
    session.feature("agent").publish("interrupt", {});
    return true;
  };

  // A control command applies its `value` arg directly (palette / Claude), or opens the picker when bare.
  const registerSelect = (commandId: string, axis: string): (() => void) =>
    registerCommand(commandId, (args: unknown) => {
      const session = props.session;
      if (session === null) {
        return false;
      }
      const value = (args as { value?: unknown } | undefined)?.value;
      if (typeof value === "string" && value.length > 0) {
        setAgentControl(session, axis, value);
      } else {
        openControlPicker(axis);
      }
      return true;
    });

  // A decision command answers the same approval the card chips advertise (turn-progress.pendingApproval).
  const registerDecision = (commandId: string, decision: string): (() => void) =>
    registerCommand(commandId, () => {
      const session = props.session;
      if (session === null || props.pendingApprovalId === null) {
        return false;
      }
      session.feature("agent").publish("approval", {
        requestId: props.pendingApprovalId,
        decision,
      });
      return true;
    });

  // Applies a `value` arg to an axis directly (palette / Claude), or opens the merged model picker when bare.
  const registerModelSelect = (commandId: string, axis: string): (() => void) =>
    registerCommand(commandId, (args: unknown) => {
      const session = props.session;
      if (session === null) {
        return false;
      }
      const value = (args as { value?: unknown } | undefined)?.value;
      if (typeof value === "string" && value.length > 0) {
        setAgentControl(session, axis, value);
      } else {
        openControlPicker(MODEL_AXIS);
      }
      return true;
    });

  const offPaste = registerCommand(CommandIds.agentPaste, paste);
  const offSubmit = registerCommand(CommandIds.agentSubmit, submit);
  const offInterrupt = registerCommand(CommandIds.agentInterrupt, interrupt);
  const offOpenPlan = registerCommand(CommandIds.openAgentPlan, (args) => {
    const session = props.session;
    if (session === null) {
      return false;
    }
    const supplied = planIdentityArgsSupplied(args);
    const plan = supplied ? planIdentityFromArgs(args) : props.latestPlan;
    if (plan === null) {
      notify(
        "info",
        supplied ? "That plan is no longer available." : "No completed plan is available yet.",
      );
      return true;
    }
    void session.feature("agent").request("openPlan", plan);
    return true;
  });
  const offTogglePlan = registerCommand(CommandIds.togglePlanMode, () => {
    const session = props.session;
    return session !== null && toggleAgentControl(session, CommandIds.togglePlanMode);
  });
  // Model and Effort share one cascading picker; a value arg applies directly.
  const offSelectModel = registerModelSelect(CommandIds.selectModel, "model");
  const offSelectEffort = registerModelSelect(CommandIds.selectEffort, "effort");
  const offSelectApproval = registerSelect(CommandIds.selectApprovalPolicy, "approvalPolicy");
  const offSelectSandbox = registerSelect(CommandIds.selectSandbox, "sandbox");
  // Fast Mode toggles the active model's service tier without opening a picker.
  const offToggleFast = registerCommand(CommandIds.toggleFastMode, () => {
    const session = props.session;
    if (session === null) {
      return false;
    }
    const model = currentModel(session);
    if (model === undefined || model.fastTier === "") {
      return false;
    }
    toggleModelFast(session, model);
    return true;
  });
  const offApprove = registerDecision(CommandIds.agentApprove, "accept");
  const offApproveForSession = registerDecision(
    CommandIds.agentApproveForSession,
    "acceptForSession",
  );
  const offDecline = registerDecision(CommandIds.agentDecline, "decline");
  onCleanup(offPaste);
  onCleanup(offSubmit);
  onCleanup(offInterrupt);
  onCleanup(offOpenPlan);
  onCleanup(offTogglePlan);
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
      <Show when={!props.compact}>
        <AgentWorkingStatus
          compact={false}
          pendingKind={props.pendingKind}
          turnActive={props.turnActive}
          turnStartedAt={props.turnStartedAt}
        />
      </Show>
      <Show when={composer().attachments.length > 0}>
        <AgentAttachmentStrip
          attachments={composer().attachments}
          onRemove={(id) => {
            if (props.session !== null) {
              removeComposerAttachment(props.session, id);
            }
          }}
        />
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
                    if (props.session !== null) {
                      unstageSkill(props.session, skill);
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
      <textarea
        ref={textareaRef}
        rows={1}
        value={composer().draft}
        placeholder={
          props.turnActive
            ? "Steer the running turn…"
            : "Write a prompt — / for commands and skills"
        }
        onKeyDown={onComposerKeyDown}
        onInput={(event) => {
          if (props.session !== null) {
            setComposerDraft(props.session, event.currentTarget.value);
            props.session.feature("agent").publish("typing", {});
            // Editing starts a fresh draft, ending any history browse.
            if (historyCursor().cursor !== null) {
              setHistoryCursor(IDLE_CURSOR);
            }
          }
        }}
        onPaste={(event) => {
          if (props.session !== null) {
            if (props.inputProtocol >= 2) {
              captureAgentImagePaste(event, props.session);
            } else {
              sendPastedImagesFromClipboard(event, props.session);
            }
          }
        }}
      />
      <div class="agent-compose-actions">
        <Show when={canInterrupt()}>
          <button
            type="button"
            aria-label="Interrupt"
            title={`Interrupt the running turn${keyHint(CommandIds.agentInterrupt)}`}
            onClick={() => void dispatchCommand(CommandIds.agentInterrupt)}
          >
            <span class="mobile-action-wide">Interrupt</span>
            <span class="mobile-action-compact mobile-action-stop" aria-hidden="true" />
          </button>
        </Show>
        <button
          type="submit"
          class="mobile-primary-action"
          aria-label={props.turnActive ? "Steer" : "Run"}
          title={`${props.turnActive ? "Steer the running turn" : "Run prompt"}${props.compact ? "" : keyHint(CommandIds.agentSubmit)}`}
          disabled={!canSubmit()}
        >
          <span class="mobile-action-wide">
            {composer().submittingId !== null ? "Sending…" : props.turnActive ? "Steer" : "Run"}
          </span>
          <span class="mobile-action-compact mobile-action-submit" aria-hidden="true" />
        </button>
      </div>
      <Show when={composer().error !== null}>
        <div class="agent-compose-error">{composer().error}</div>
      </Show>
    </form>
  );
}
