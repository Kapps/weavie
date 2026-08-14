import { createEffect, createMemo, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { liveKeyLabel } from "../commands/keys-live";
import { registerCommand, runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { agentInputDraft, agentInputRequestKey } from "./AgentInputDrafts";
import { AgentQuestionControl } from "./AgentQuestionControl";
import { inputQuestions } from "./input-questions";

export function ApprovalActions(props: {
  session: ClientSession | null;
  message: AgentPaneUpdate;
  // The chords answer only the newest pending approval; older cards must not advertise them.
  answersToKeys: boolean;
}): JSX.Element {
  const choose = (optionId: string): void => {
    const requestId = props.message.itemId;
    if (
      props.session !== null &&
      requestId !== null &&
      requestId !== undefined &&
      requestId.length > 0
    ) {
      props.session.feature("agent").publish("permission", { requestId, optionId });
    }
  };

  const commandFor = (kind: string): string | null => {
    switch (kind) {
      case "allow_once":
        return CommandIds.agentApprove;
      case "allow_always":
        return CommandIds.agentApproveForSession;
      case "reject_once":
      case "reject_always":
        return CommandIds.agentDecline;
      default:
        return null;
    }
  };

  createEffect(() => {
    if (!props.answersToKeys) {
      return;
    }
    const registrations = new Map<string, string>();
    for (const action of props.message.actions ?? []) {
      const command = commandFor(action.kind);
      if (command !== null && !registrations.has(command)) {
        registrations.set(command, action.id);
      }
    }
    const off = [...registrations].map(([command, optionId]) =>
      registerCommand(command, () => {
        choose(optionId);
        return true;
      }),
    );
    onCleanup(() => off.forEach((dispose) => void dispose()));
  });

  const decision = (label: string, value: string, kind: string): JSX.Element => {
    const commandId = commandFor(kind);
    const key = (): string =>
      props.answersToKeys && commandId !== null ? liveKeyLabel(commandId) : "";
    return (
      <button
        type="button"
        title={key() === "" ? label : `${label} (${key()})`}
        onClick={() => choose(value)}
      >
        {label}
        <Show when={key() !== ""}>
          <kbd class="agent-key-chip">{key()}</kbd>
        </Show>
      </button>
    );
  };

  return (
    <div class="agent-approval-actions">
      <For each={props.message.actions ?? []}>
        {(action) => decision(action.label, action.id, action.kind)}
      </For>
    </div>
  );
}

export function AuthenticationActions(props: {
  session: ClientSession;
  message: AgentPaneUpdate;
}): JSX.Element {
  const authenticate = (methodId: string): boolean => {
    props.session.feature("agent").publish("authenticate", { methodId, answers: {} });
    return true;
  };
  createEffect(() => {
    const first = props.message.actions?.[0];
    if (first === undefined) {
      return;
    }
    const off = registerCommand(CommandIds.agentAuthenticate, () => authenticate(first.id));
    onCleanup(off);
  });
  const firstId = (): string | null => props.message.actions?.[0]?.id ?? null;
  return (
    <div class="agent-approval-actions">
      <For each={props.message.actions ?? []}>
        {(action) => (
          <button
            type="button"
            title={
              action.id === firstId() && liveKeyLabel(CommandIds.agentAuthenticate) !== ""
                ? `${action.label} (${liveKeyLabel(CommandIds.agentAuthenticate)})`
                : action.label
            }
            onClick={() => authenticate(action.id)}
          >
            {action.label}
          </button>
        )}
      </For>
    </div>
  );
}

export function UrlInputActions(props: {
  session: ClientSession;
  message: AgentPaneUpdate;
  answersToKeys: boolean;
}): JSX.Element {
  const resolve = (action: "accept" | "decline" | "cancel"): boolean => {
    const requestId = props.message.itemId;
    if (requestId === null || requestId === undefined || requestId.length === 0) {
      return false;
    }
    props.session.feature("agent").publish("input", { requestId, action, answers: {} });
    return true;
  };
  createEffect(() => {
    if (!props.answersToKeys) {
      return;
    }
    const offDecline = registerCommand(CommandIds.agentDeclineInput, () => resolve("decline"));
    const offCancel = registerCommand(CommandIds.agentCancelInput, () => resolve("cancel"));
    onCleanup(() => {
      offDecline();
      offCancel();
    });
  });
  const open = async (): Promise<void> => {
    const url = props.message.resourceUri;
    const requestId = props.message.itemId;
    if (url === null || url === undefined || requestId === null || requestId === undefined) {
      return;
    }
    const result = await runCommandWithFeedback(CommandIds.openUrlExternal, { url });
    if (result.ok) {
      resolve("accept");
    }
  };
  const declineKey = (): string =>
    props.answersToKeys ? liveKeyLabel(CommandIds.agentDeclineInput) : "";
  const cancelKey = (): string =>
    props.answersToKeys ? liveKeyLabel(CommandIds.agentCancelInput) : "";
  return (
    <div class="agent-approval-actions">
      <button
        type="button"
        title={`Open link in your browser${liveKeyLabel(CommandIds.openUrlExternal) === "" ? "" : ` (${liveKeyLabel(CommandIds.openUrlExternal)})`}`}
        onClick={() => void open()}
      >
        {props.message.actions?.[0]?.label ?? "Open link"}
      </button>
      <button
        type="button"
        title={declineKey() === "" ? "Decline request" : `Decline request (${declineKey()})`}
        onClick={() => resolve("decline")}
      >
        Decline
      </button>
      <button
        type="button"
        title={cancelKey() === "" ? "Cancel request" : `Cancel request (${cancelKey()})`}
        onClick={() => resolve("cancel")}
      >
        Cancel
      </button>
    </div>
  );
}

export function InputRequestActions(props: {
  session: ClientSession;
  message: AgentPaneUpdate;
  answersToKeys: boolean;
}): JSX.Element {
  if (props.message.itemType === "url") {
    return (
      <UrlInputActions
        session={props.session}
        message={props.message}
        answersToKeys={props.answersToKeys}
      />
    );
  }
  const questions = createMemo(() => inputQuestions(props.message));
  const requestKey = agentInputRequestKey(props.message);
  const draft = agentInputDraft(props.session, requestKey, questions());
  let form: HTMLFormElement | undefined;

  const resolve = (
    action: "accept" | "decline" | "cancel",
    answers: Record<string, string[]>,
  ): boolean => {
    const requestId = props.message.itemId;
    if (requestId === null || requestId === undefined || requestId.length === 0) {
      return false;
    }
    props.session.feature("agent").publish("input", { requestId, action, answers });
    return true;
  };
  const submit = (): boolean => resolve("accept", draft.answers());
  const requestSubmit = (): boolean => {
    if (form === undefined) return false;
    form.requestSubmit();
    return true;
  };
  createEffect(() => {
    if (!props.answersToKeys) {
      return;
    }
    const offDecline = registerCommand(CommandIds.agentDeclineInput, () => resolve("decline", {}));
    const offCancel = registerCommand(CommandIds.agentCancelInput, () => resolve("cancel", {}));
    const offAccept = registerCommand(CommandIds.agentAcceptInput, requestSubmit);
    onCleanup(() => {
      offDecline();
      offCancel();
      offAccept();
    });
  });
  const declineKey = (): string =>
    props.answersToKeys ? liveKeyLabel(CommandIds.agentDeclineInput) : "";
  const cancelKey = (): string =>
    props.answersToKeys ? liveKeyLabel(CommandIds.agentCancelInput) : "";
  const acceptKey = (): string =>
    props.answersToKeys ? liveKeyLabel(CommandIds.agentAcceptInput) : "";

  const setAnswer = (id: string, values: string[]): void => {
    draft.setAnswers({ ...draft.answers(), [id]: values });
  };

  return (
    <form
      ref={form}
      class="agent-input-request"
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <For each={questions()}>
        {(question) => (
          <fieldset class="agent-input-question">
            <legend>{question.header.length > 0 ? question.header : question.question}</legend>
            <small>{question.question}</small>
            <AgentQuestionControl
              question={question}
              values={draft.answers()[question.id] ?? []}
              setValues={(values) => setAnswer(question.id, values)}
            />
            <Show when={question.options.length > 0}>
              <small>{question.options.map((option) => option.description).join(" ")}</small>
            </Show>
          </fieldset>
        )}
      </For>
      <div class="agent-approval-actions">
        <button
          type="submit"
          title={acceptKey() === "" ? "Submit answers" : `Submit answers (${acceptKey()})`}
        >
          Submit answers
        </button>
        <button
          type="button"
          title={declineKey() === "" ? "Decline request" : `Decline request (${declineKey()})`}
          onClick={() => resolve("decline", {})}
        >
          Decline
        </button>
        <button
          type="button"
          title={cancelKey() === "" ? "Cancel request" : `Cancel request (${cancelKey()})`}
          onClick={() => resolve("cancel", {})}
        >
          Cancel
        </button>
      </div>
    </form>
  );
}
