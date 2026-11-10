import { createMemo, For, type JSX, Match, Switch } from "solid-js";
import { liveKeyLabel } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ChoiceRequestActions } from "./AgentChoiceRequest";
import { agentInputDraft, agentInputRequestKey } from "./AgentInputDrafts";
import { AgentQuestionControl } from "./AgentQuestionControl";
import { inputQuestions, singleChoiceQuestion } from "./input-questions";
import {
  DismissButtons,
  type InputRequestProps,
  ownedKeyLabel,
  resolveInput,
  useInputCommands,
  withKey,
} from "./input-request";

export function InputRequestActions(props: InputRequestProps): JSX.Element {
  return (
    <Switch fallback={<InputForm {...props} />}>
      <Match when={props.message.itemType === "url"}>
        <UrlInputActions {...props} />
      </Match>
      <Match when={singleChoiceQuestion(props.message)}>
        {(question) => <ChoiceRequestActions {...props} question={question()} />}
      </Match>
    </Switch>
  );
}

function UrlInputActions(props: InputRequestProps): JSX.Element {
  useInputCommands(props, () => new Map());
  const open = async (): Promise<void> => {
    const url = props.message.resourceUri;
    if (url === null || url === undefined) {
      return;
    }
    const result = await runCommandWithFeedback(CommandIds.openUrlExternal, { url });
    if (result.ok) {
      resolveInput(props, "accept", {});
    }
  };
  return (
    <div class="agent-approval-actions">
      <button
        type="button"
        class="default ui-button"
        title={withKey("Open link in your browser", liveKeyLabel(CommandIds.openUrlExternal))}
        onClick={() => void open()}
      >
        {props.message.actions?.[0]?.label ?? "Open link"}
      </button>
      <DismissButtons {...props} buttonClass="ui-button" />
    </div>
  );
}

function InputForm(props: InputRequestProps): JSX.Element {
  const questions = createMemo(() => inputQuestions(props.message));
  const draft = agentInputDraft(props.session, agentInputRequestKey(props.message), questions());
  let form: HTMLFormElement | undefined;
  useInputCommands(
    props,
    () =>
      new Map([
        [
          CommandIds.agentAcceptInput,
          () => {
            form?.requestSubmit();
            return form !== undefined;
          },
        ],
      ]),
  );

  return (
    <form
      ref={form}
      class="agent-input-request"
      onSubmit={(event) => {
        event.preventDefault();
        resolveInput(props, "accept", draft.answers());
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
              setValues={(values) =>
                draft.setAnswers({ ...draft.answers(), [question.id]: values })
              }
            />
          </fieldset>
        )}
      </For>
      <div class="agent-approval-actions">
        <button
          type="submit"
          class="default ui-button"
          title={withKey("Submit answers", ownedKeyLabel(props, CommandIds.agentAcceptInput))}
        >
          Submit answers
        </button>
        <DismissButtons {...props} buttonClass="ui-button" />
      </div>
    </form>
  );
}
