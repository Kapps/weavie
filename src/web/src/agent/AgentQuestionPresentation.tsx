import { For, type JSX, Show } from "solid-js";
import type { AgentInputQuestion, AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentLinkedText } from "./AgentPaneLinks";
import { inputRequestContext, questionDescription, questionTitle } from "./input-questions";
import { DismissButtons, type InputRequestProps } from "./input-request";

export function QuestionHeading(props: {
  question: AgentInputQuestion;
  message: AgentPaneUpdate;
}): JSX.Element {
  return (
    <>
      <span class="agent-question-title">{questionTitle(props.question)}</span>
      <Show when={questionDescription(props.question, props.message)}>
        {(description) => <span class="agent-question-description">{description()}</span>}
      </Show>
    </>
  );
}

export function InputRequestContext(props: {
  message: AgentPaneUpdate;
  session: ClientSession;
}): JSX.Element {
  return (
    <Show when={inputRequestContext(props.message).length > 0}>
      <div class="agent-question-context">
        <For each={inputRequestContext(props.message)}>
          {(text) => (
            <p>
              <AgentLinkedText session={props.session} text={text} />
            </p>
          )}
        </For>
      </div>
    </Show>
  );
}

export function QuestionFooter(props: InputRequestProps & { children: JSX.Element }): JSX.Element {
  return (
    <div class="agent-question-footer">
      <InputRequestContext message={props.message} session={props.session} />
      <div class="agent-question-actions ui-button-group">
        {props.children}
        <DismissButtons {...props} />
      </div>
    </div>
  );
}
