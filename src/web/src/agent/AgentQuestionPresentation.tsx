import { type JSX, Show } from "solid-js";
import type { AgentInputQuestion, AgentPaneUpdate, ClientSession } from "../bridge";
import { AgentMarkdown } from "./AgentMarkdown";
import {
  inputRequestPrompt,
  questionDescription,
  questionTitle,
  singleChoiceQuestion,
} from "./input-questions";
import { DismissButtons, type InputRequestProps } from "./input-request";

// Question text is agent-written, so it reads as markdown like a reply does.
function QuestionProse(props: {
  class: string;
  cacheKey: object;
  content: string;
  session: ClientSession;
}): JSX.Element {
  return (
    <Show when={props.content !== ""}>
      <div class={props.class}>
        <AgentMarkdown
          reply={null}
          cacheKey={props.cacheKey}
          content={props.content}
          flavor="agent"
          renderMermaid
          session={props.session}
        />
      </div>
    </Show>
  );
}

// One pick-one question reads title, then prompt, then options; any other request opens with its prompt.
function RequestPrompt(props: {
  message: AgentPaneUpdate;
  session: ClientSession;
  underHeading: boolean;
}): JSX.Element {
  return (
    <Show when={(singleChoiceQuestion(props.message) !== null) === props.underHeading}>
      <QuestionProse
        class="agent-question-prompt"
        cacheKey={props.message}
        content={inputRequestPrompt(props.message)}
        session={props.session}
      />
    </Show>
  );
}

export function QuestionTitle(props: { question: AgentInputQuestion }): JSX.Element {
  return <span class="agent-question-title">{questionTitle(props.question)}</span>;
}

interface QuestionProps {
  question: AgentInputQuestion;
  message: AgentPaneUpdate;
  session: ClientSession;
}

/** What follows a question's title: its description, and the prompt of a single pick-one request. */
export function QuestionDetail(props: QuestionProps): JSX.Element {
  return (
    <>
      <QuestionProse
        class="agent-question-description"
        cacheKey={props.question}
        content={questionDescription(props.question, props.message)}
        session={props.session}
      />
      <RequestPrompt message={props.message} session={props.session} underHeading />
    </>
  );
}

export function QuestionHeading(props: QuestionProps): JSX.Element {
  return (
    <>
      <QuestionTitle question={props.question} />
      <QuestionDetail {...props} />
    </>
  );
}

export function InputRequestPrompt(props: {
  message: AgentPaneUpdate;
  session: ClientSession;
}): JSX.Element {
  return <RequestPrompt {...props} underHeading={false} />;
}

export function QuestionActions(props: InputRequestProps & { children: JSX.Element }): JSX.Element {
  return (
    <div class="agent-question-actions ui-button-group">
      {props.children}
      <DismissButtons {...props} />
    </div>
  );
}
