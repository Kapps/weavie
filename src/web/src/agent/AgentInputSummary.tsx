import { For, type JSX, Show } from "solid-js";
import type { AgentInputQuestion, AgentPaneUpdate } from "../bridge";
import { Disclosure } from "./AgentDisclosure";
import { inputQuestions } from "./input-questions";

/** A resolved input request reopened: the prompt exactly as asked, every option, and what was answered. */
export function ResolvedInputSummary(props: {
  expanded: boolean;
  message: AgentPaneUpdate;
  onToggle: (open: boolean) => void;
}): JSX.Element {
  const answersFor = (question: AgentInputQuestion): string[] =>
    props.message.answers?.[question.id] ?? [];
  const others = (question: AgentInputQuestion): string[] =>
    answersFor(question).filter(
      (answer) => !question.options.some((option) => option.value === answer),
    );

  return (
    <Disclosure
      class="agent-request-details"
      label={props.expanded ? "hide prompt" : "show prompt and options"}
      open={props.expanded}
      onToggle={props.onToggle}
    >
      <div class="agent-input-review">
        <For each={inputQuestions(props.message)}>
          {(question) => (
            <div class="agent-input-reviewed-question">
              <span class="agent-input-reviewed-prompt">{question.question}</span>
              <Show when={question.options.length > 0}>
                <div class="agent-input-choices">
                  <For each={question.options}>
                    {(option) => (
                      <div
                        class="agent-input-choice"
                        classList={{ chosen: answersFor(question).includes(option.value) }}
                      >
                        <span>{option.label}</span>
                        <Show when={option.description.length > 0}>
                          <small>{option.description}</small>
                        </Show>
                      </div>
                    )}
                  </For>
                  <For each={others(question)}>
                    {(answer) => (
                      <div class="agent-input-choice chosen">
                        <span>{answer}</span>
                        <small>typed answer</small>
                      </div>
                    )}
                  </For>
                </div>
              </Show>
              <Show when={question.options.length === 0}>
                <span class="agent-input-reviewed-answer">
                  {answersFor(question).join(", ") || "no answer"}
                </span>
              </Show>
            </div>
          )}
        </For>
      </div>
    </Disclosure>
  );
}
