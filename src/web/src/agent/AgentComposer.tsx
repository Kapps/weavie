import {
  createEffect,
  createMemo,
  createSignal,
  For,
  type JSX,
  onCleanup,
  onMount,
  Show,
} from "solid-js";
import type { AgentSlashEntry, ClientSession } from "../bridge";
import { setContext } from "../commands/context";
import { keyHint } from "../commands/key-hint";
import { dispatchCommand, registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { sendPastedImage, sendPastedImagesFromClipboard } from "../terminal/paste-image";
import { AgentAttachmentStrip } from "./AgentAttachmentStrip";
import { AgentSlashMenu } from "./AgentSlashMenu";
import { AgentWorkingStatus } from "./AgentWorkingStatus";
import { agentControlForCommand } from "./agent-control-commands";
import { agentControlState, openControlPicker, setAgentControl } from "./agent-controls-store";
import {
  type AgentPlanIdentity,
  planIdentityArgsSupplied,
  planIdentityFromArgs,
} from "./agent-plan";
import { agentQueuedSubmissions, queuedSubmissionLabel } from "./agent-queue-store";
import { registerComposerPasteTarget } from "./composer-clipboard";
import {
  captureAgentImagePaste,
  composerState,
  removeComposerAttachment,
  setComposerDraft,
  setComposerError,
  submitAgentAside,
  submitAgentTurn,
  uploadAgentImage,
} from "./composer-store";
import { agentImageBlob } from "./pasted-images";
import {
  type HistoryCursor,
  type HistoryRecall,
  IDLE_CURSOR,
  recallNext,
  recallPrevious,
} from "./prompt-history";
import {
  classifyAgentDraft,
  classifyAgentSubmission,
  filterSlash,
  invocationForAction,
  slashCompletionScope,
  slashQuery,
  weavieCommandInput,
} from "./slash";
import { caretOnFirstVisualLine, caretOnLastVisualLine } from "./textarea-lines";
import type { PendingRequestKind } from "./turn-progress";

export function AgentComposer(props: {
  active: boolean;
  compact: boolean;
  history: readonly string[];
  inputProtocol: number;
  interruptible: boolean;
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
  const queued = createMemo(() => agentQueuedSubmissions(props.session));
  const canInterrupt = createMemo(() => props.session !== null && props.interruptible);

  createEffect(() => setContext("agentApprovalPending", props.pendingKind === "approval"));
  createEffect(() => setContext("agentInputPending", props.pendingKind === "input"));
  createEffect(() =>
    setContext("agentAuthenticationPending", props.pendingKind === "authentication"),
  );
  onCleanup(() => {
    setContext("agentApprovalPending", false);
    setContext("agentInputPending", false);
    setContext("agentAuthenticationPending", false);
  });

  const draftAction = createMemo(() =>
    classifyAgentDraft(agentControlState(props.session), composer().draft),
  );
  const submissionAction = createMemo(() =>
    classifyAgentSubmission(agentControlState(props.session), composer().draft),
  );

  const canSubmit = createMemo(() => {
    const action = submissionAction();
    if (action.kind === "loading") return false;
    const state = composer();
    if (props.inputProtocol < 2) {
      return (
        props.session !== null &&
        (state.draft.trim().length > 0 || props.pendingLegacyImageCount > 0)
      );
    }
    if (props.session === null || state.pendingSubmission !== null) return false;
    if (
      action.kind === "command" &&
      action.entry.kind !== "mcpPrompt" &&
      action.entry.commandId !== CommandIds.askAgentAside
    )
      return true;
    return (
      state.attachments.every((attachment) => attachment.status === "ready") &&
      (state.draft.trim().length > 0 || state.attachments.length > 0)
    );
  });

  const [historyCursor, setHistoryCursor] = createSignal<HistoryCursor>(IDLE_CURSOR);
  // Switching sessions abandons any in-progress history browse.
  createEffect(() => {
    props.session;
    setHistoryCursor(IDLE_CURSOR);
  });

  const [slashDismissed, setSlashDismissed] = createSignal(false);
  const slashScope = createMemo(() =>
    slashCompletionScope(agentControlState(props.session).slash, composer().draft),
  );
  const slashText = createMemo(() => slashQuery(slashScope().draft));
  const slashEntries = createMemo(() => {
    const query = slashText();
    return query === null || slashDismissed() ? [] : filterSlash(slashScope().entries, query);
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

  const acceptSlash = (entry: AgentSlashEntry, execute: boolean): void => {
    const session = props.session;
    if (session === null) {
      return;
    }
    if (entry.kind === "weavieCommand") {
      if (entry.inputName === null) {
        setComposerDraft(session, "");
        void runCommandWithFeedback(entry.commandId);
      } else {
        const draft = `/${entry.name} `;
        setComposerDraft(session, draft);
        placeCaretAfterDraftUpdate(draft, draft.length);
      }
    } else {
      const draft = `${slashScope().prefix}/${entry.name}${entry.inputHint === null ? "" : " "}`;
      setComposerDraft(session, draft);
      if (execute) submit();
      else placeCaretAfterDraftUpdate(draft, draft.length);
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

  onMount(() => {
    onCleanup(
      registerComposerPasteTarget(textareaRef!, () => {
        const session = props.session;
        if (session === null) return null;
        const inputProtocol = props.inputProtocol;
        return {
          session,
          draft: () => composerState(session).draft,
          setDraft: (draft) => setComposerDraft(session, draft),
          pasteImage: (mime, dataB64) => {
            if (inputProtocol >= 2) uploadAgentImage(session, agentImageBlob(mime, dataB64));
            else sendPastedImage(session, mime, dataB64);
          },
        };
      }),
    );
  });

  const submit = (): boolean => {
    const session = props.session;
    if (!props.active || session === null) {
      return false;
    }
    const action = draftAction();
    if (action.kind === "loading") return true;
    const weavieCommand = action.kind === "command" ? action.entry : null;
    if (weavieCommand?.kind === "weavieCommand") {
      const input = weavieCommandInput(weavieCommand, composer().draft);
      if (weavieCommand.commandId === CommandIds.askAgentAside) {
        const inner = submissionAction();
        if (inner.kind === "loading") return true;
        if (inner.kind === "command" && inner.entry.kind === "weavieCommand") {
          setComposerError(session, `/${inner.entry.name} cannot run in a side conversation.`);
          return true;
        }
        if (!submitAgentAside(session, input ?? "", invocationForAction(inner))) return false;
        setHistoryCursor(IDLE_CURSOR);
        props.onSubmitted();
        return true;
      }
      if (weavieCommand.inputName !== null && input === null) {
        setComposerError(
          session,
          `${weavieCommand.name} requires ${weavieCommand.inputHint ?? "input"}.`,
        );
        return false;
      }
      const args =
        weavieCommand.inputName === null || input === null
          ? undefined
          : { [weavieCommand.inputName]: input };
      setComposerDraft(session, "");
      void runCommandWithFeedback(weavieCommand.commandId, args);
      setHistoryCursor(IDLE_CURSOR);
      return true;
    }
    if (props.inputProtocol < 2) {
      const state = composerState(session);
      if (state.draft.trim().length === 0 && props.pendingLegacyImageCount === 0) {
        return false;
      }
      session.feature("agent").publish("submit", {
        id: "",
        prompt: state.draft.trim(),
        kind: "prompt",
        commandName: "",
        attachmentIds: [],
      });
      setComposerDraft(session, "");
    } else {
      if (!submitAgentTurn(session, invocationForAction(action))) return false;
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

  // A semantic command targets the matching ACP-owned option without assuming its transport shape.
  const registerSelect = (commandId: string): (() => void) =>
    registerCommand(commandId, (args: unknown) => {
      const session = props.session;
      if (session === null) {
        return false;
      }
      const axis = agentControlForCommand(agentControlState(session).axes, commandId);
      if (axis === undefined) {
        return false;
      }
      const value = (args as { value?: unknown } | undefined)?.value;
      if (typeof value === "string" && value.length > 0) {
        setAgentControl(session, axis.id, value);
      } else {
        openControlPicker(axis.id);
      }
      return true;
    });

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
    if (session === null) {
      return false;
    }
    const mode = agentControlForCommand(agentControlState(session).axes, CommandIds.togglePlanMode);
    const plan = mode?.options.find((option) => option.id === "plan");
    const other = mode?.options.find((option) => option.id !== "plan");
    const target = mode?.value === "plan" ? other : plan;
    if (mode === undefined || target === undefined) {
      return false;
    }
    setAgentControl(session, mode.id, target.id);
    return true;
  });
  const offSelectModel = registerSelect(CommandIds.selectModel);
  const offSelectEffort = registerSelect(CommandIds.selectEffort);
  const offSelectApproval = registerSelect(CommandIds.selectApprovalPolicy);
  const offSelectSandbox = registerSelect(CommandIds.selectSandbox);
  const offToggleFast = registerCommand(CommandIds.toggleFastMode, () => {
    const session = props.session;
    if (session === null) {
      return false;
    }
    const fast = agentControlForCommand(agentControlState(session).axes, CommandIds.toggleFastMode);
    const target = fast?.options.find((option) => option.id !== fast.value);
    if (fast === undefined || target === undefined) {
      return false;
    }
    setAgentControl(session, fast.id, target.id);
    return true;
  });
  onCleanup(offSubmit);
  onCleanup(offInterrupt);
  onCleanup(offOpenPlan);
  onCleanup(offTogglePlan);
  onCleanup(offSelectModel);
  onCleanup(offSelectApproval);
  onCleanup(offSelectSandbox);
  onCleanup(offSelectEffort);
  onCleanup(offToggleFast);

  return (
    <form
      class="agent-compose"
      data-agent-composer
      data-agent-controls-ready={agentControlState(props.session).ready}
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
      <Show when={queued().length > 0}>
        <div class="agent-compose-queued">
          <span>Queued</span>
          <For each={queued()}>
            {(submission) => (
              <span title={queuedSubmissionLabel(submission)}>
                {queuedSubmissionLabel(submission)}
              </span>
            )}
          </For>
        </div>
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
      <AgentSlashMenu
        entries={slashEntries()}
        onAccept={acceptSlash}
        onDismiss={() => setSlashDismissed(true)}
      />
      <textarea
        ref={textareaRef}
        data-agent-paste-target
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
            {composer().pendingSubmission !== null
              ? "Sending…"
              : props.turnActive
                ? "Steer"
                : "Run"}
          </span>
          <span class="mobile-action-compact mobile-action-submit" aria-hidden="true" />
        </button>
      </div>
      <Show when={draftAction().kind === "loading"}>
        <div class="agent-compose-queued" role="status">
          Agent commands are loading…
        </div>
      </Show>
      <Show when={composer().error !== null}>
        <div class="agent-compose-error">{composer().error}</div>
      </Show>
    </form>
  );
}
