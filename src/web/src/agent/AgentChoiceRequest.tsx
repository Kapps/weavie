import { createEffect, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentInputQuestion } from "../bridge";
import { setContext } from "../commands/context";
import { liveIndexedKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { agentInputDraft, agentInputRequestKey } from "./AgentInputDrafts";
import { QuestionFooter, QuestionHeading } from "./AgentQuestionPresentation";
import {
  type InputRequestProps,
  ownedKeyLabel,
  resolveInput,
  useInputCommands,
  withKey,
} from "./input-request";

/** One pick-one question: each option is a row that answers on click or its key; "Other" is typed below. */
export function ChoiceRequestActions(
  props: InputRequestProps & { question: AgentInputQuestion },
): JSX.Element {
  const id = props.question.id;
  // The draft holds only the typed Other answer; picking a row answers at once.
  const draft = agentInputDraft(props.session, agentInputRequestKey(props.message), []);
  const other = (): string => draft.answers()[id]?.[0] ?? "";
  const preferred = (): string | undefined =>
    props.question.initialValues.find((value) =>
      props.question.options.some((option) => option.value === value),
    ) ?? props.question.options[0]?.value;
  const answer = (value: string): boolean => resolveInput(props, "accept", { [id]: [value] });

  useInputCommands(
    props,
    () =>
      new Map([
        [
          CommandIds.agentAnswerByIndex,
          (args: unknown) => {
            const index = Number((args as { index?: unknown } | undefined)?.index);
            const option = props.question.options[index - 1];
            return option !== undefined && answer(option.value);
          },
        ],
        [CommandIds.agentAcceptInput, () => answer(other().trim() || (preferred() ?? ""))],
      ]),
  );
  createEffect(() => {
    if (!props.answersToKeys) return;
    setContext("agentChoicePending", true);
    onCleanup(() => setContext("agentChoicePending", false));
  });

  const indexKey = (index: number): string =>
    props.answersToKeys ? liveIndexedKeyLabel(CommandIds.agentAnswerByIndex, index + 1) : "";
  const rowKeys = (index: number, value: string): string =>
    [
      indexKey(index),
      value === preferred() && other().trim() === ""
        ? ownedKeyLabel(props, CommandIds.agentAcceptInput)
        : "",
    ]
      .filter((key) => key !== "")
      .join(", ");

  return (
    <div class="agent-choice-request">
      <div class="agent-choice-head">
        <QuestionHeading question={props.question} message={props.message} />
      </div>
      <For each={props.question.options}>
        {(option, index) => (
          <button
            type="button"
            class="agent-choice-row ui-row ui-row-stack"
            classList={{ default: option.value === preferred() }}
            title={withKey(option.label, rowKeys(index(), option.value))}
            onClick={() => answer(option.value)}
          >
            <span class="ui-row-text">
              <span class="agent-choice-label">{option.label}</span>
              <Show when={option.description}>
                <span class="ui-row-desc">{option.description}</span>
              </Show>
            </span>
            <Show when={indexKey(index())}>
              {(key) => (
                <span class="agent-choice-key ui-kbd" aria-hidden="true">
                  {key()}
                </span>
              )}
            </Show>
          </button>
        )}
      </For>
      <Show when={props.question.allowsOther}>
        <form
          class="agent-choice-custom"
          onSubmit={(event) => {
            event.preventDefault();
            if (other().trim() !== "") answer(other().trim());
          }}
        >
          <input
            class="agent-choice-other ui-field"
            type="text"
            placeholder="Other…"
            aria-label="Other answer"
            value={other()}
            onInput={(event) => draft.setAnswers({ [id]: [event.currentTarget.value] })}
          />
          <button
            type="submit"
            class="ui-button ui-button-lg"
            disabled={other().trim() === ""}
            title={withKey("Send answer", ownedKeyLabel(props, CommandIds.agentAcceptInput))}
          >
            Send
          </button>
        </form>
      </Show>
      <QuestionFooter {...props}>{null}</QuestionFooter>
    </div>
  );
}
