import { createEffect, For, type JSX, onCleanup, Show } from "solid-js";
import type { AgentInputQuestion } from "../bridge";
import { setContext } from "../commands/context";
import { liveIndexedKeyLabel } from "../commands/keys-live";
import { CommandIds } from "../commands/types";
import { agentInputDraft, agentInputRequestKey } from "./AgentInputDrafts";
import {
  DismissButtons,
  type InputRequestProps,
  ownedKeyLabel,
  resolveInput,
  useInputCommands,
  withKey,
} from "./input-request";

/** One pick-one question: each option is a row that answers on click or Alt+N; "Other" is typed below. */
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
      value === preferred() ? ownedKeyLabel(props, CommandIds.agentAcceptInput) : "",
    ]
      .filter((key) => key !== "")
      .join(", ");

  return (
    <div class="agent-choice-request">
      <p class="agent-choice-prompt" title={props.question.header}>
        {props.question.question}
      </p>
      <For each={props.question.options}>
        {(option, index) => (
          <button
            type="button"
            class="agent-choice-row"
            classList={{ default: option.value === preferred() }}
            title={withKey(
              [option.label, option.description].filter(Boolean).join(" — "),
              rowKeys(index(), option.value),
            )}
            onClick={() => answer(option.value)}
          >
            <span class="agent-choice-label">{option.label}</span>
            <span class="agent-choice-description">{option.description}</span>
            <Show when={indexKey(index())}>
              {(key) => <kbd class="agent-key-chip">{key()}</kbd>}
            </Show>
          </button>
        )}
      </For>
      <Show when={props.question.allowsOther}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (other().trim() !== "") answer(other().trim());
          }}
        >
          <input
            class="agent-choice-other"
            type="text"
            placeholder="Other…"
            title="Type another answer (Enter)"
            value={other()}
            onInput={(event) => draft.setAnswers({ [id]: [event.currentTarget.value] })}
          />
        </form>
      </Show>
      <div class="agent-choice-dismiss">
        <DismissButtons {...props} />
      </div>
    </div>
  );
}
