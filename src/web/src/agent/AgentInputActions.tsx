import { createMemo, For, type JSX, Match, Switch } from "solid-js";
import { liveKeyLabel } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { ChoiceRequestActions } from "./AgentChoiceRequest";
import { agentInputDraft, agentInputRequestKey } from "./AgentInputDrafts";
import { AgentQuestionControl } from "./AgentQuestionControl";
import { QuestionFooter, QuestionHeading } from "./AgentQuestionPresentation";
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
    <div class="agent-approval-actions ui-button-group">
      <button
        type="button"
        class="ui-button ui-button-primary"
        title={withKey("Open link in your browser", liveKeyLabel(CommandIds.openUrlExternal))}
        onClick={() => void open()}
      >
        {props.message.actions?.[0]?.label ?? "Open link"}
      </button>
      <DismissButtons {...props} />
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
            <legend>
              <QuestionHeading question={question} message={props.message} />
            </legend>
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
      <QuestionFooter {...props}>
        <button
          type="submit"
          class="ui-button ui-button-primary"
          title={withKey("Submit answers", ownedKeyLabel(props, CommandIds.agentAcceptInput))}
        >
          Submit answers
        </button>
      </QuestionFooter>
    </form>
  );
}
