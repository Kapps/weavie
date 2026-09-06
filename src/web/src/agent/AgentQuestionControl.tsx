import { createSignal, createUniqueId, For, type JSX, Show } from "solid-js";
import type { AgentInputQuestion } from "../bridge";

export function AgentQuestionControl(props: {
  question: AgentInputQuestion;
  values: string[];
  setValues: (values: string[]) => void;
}): JSX.Element {
  const single = (): string => props.values[0] ?? "";
  if (props.question.kind === "boolean") {
    return (
      <input
        type="checkbox"
        checked={single() === "true"}
        onChange={(event) => props.setValues([String(event.currentTarget.checked)])}
      />
    );
  }
  if (props.question.options.length > 0) {
    return (
      <ChoiceList question={props.question} setValues={props.setValues} values={props.values} />
    );
  }
  if (props.question.kind === "array") {
    const values = (input: HTMLTextAreaElement): string[] =>
      input.value.split(/\r?\n/).filter((value) => value.length > 0);
    const validate = (input: HTMLTextAreaElement, count: number): void => {
      input.setCustomValidity(lengthValidity(props.question, count, "value"));
    };
    return (
      <textarea
        rows={Math.max(2, props.values.length)}
        value={props.values.join("\n")}
        placeholder="One value per line"
        ref={(input) => validate(input, props.values.length)}
        onInput={(event) => {
          const next = values(event.currentTarget);
          validate(event.currentTarget, next.length);
          props.setValues(next);
        }}
      />
    );
  }
  const type = (): "email" | "url" | "date" | "number" | "text" => {
    if (props.question.kind === "number" || props.question.kind === "integer") return "number";
    if (props.question.format === "email") return "email";
    if (props.question.format === "uri") return "url";
    if (props.question.format === "date") return "date";
    return "text";
  };
  return (
    <input
      type={type()}
      required={props.question.required}
      value={single()}
      min={props.question.minimum ?? undefined}
      max={props.question.maximum ?? undefined}
      step={
        props.question.kind === "integer" ? 1 : props.question.kind === "number" ? "any" : undefined
      }
      minLength={props.question.minimumLength ?? undefined}
      maxLength={props.question.maximumLength ?? undefined}
      pattern={props.question.pattern ?? undefined}
      onInput={(event) =>
        props.setValues(event.currentTarget.value.length > 0 ? [event.currentTarget.value] : [])
      }
    />
  );
}

/** Every advertised choice, with its own description, on screen — nothing folded behind a dropdown. */
function ChoiceList(props: {
  question: AgentInputQuestion;
  values: string[];
  setValues: (values: string[]) => void;
}): JSX.Element {
  const multiple = props.question.kind === "array";
  const group = createUniqueId();
  const isAdvertised = (value: string): boolean =>
    props.question.options.some((option) => option.value === value);
  const initialCustom = props.values.find((value) => !isAdvertised(value)) ?? "";
  const [custom, setCustom] = createSignal(props.question.allowsOther && initialCustom.length > 0);
  const [customAnswer, setCustomAnswer] = createSignal(initialCustom);
  const advertised = (): string[] => props.values.filter(isAdvertised);
  // Checkbox groups have no native minItems/maxItems, so the first box carries the group's validity.
  let counted: HTMLInputElement | undefined;
  const validate = (count: number): void => {
    if (multiple) counted?.setCustomValidity(lengthValidity(props.question, count, "option"));
  };

  const commit = (chosen: string[], other: boolean, answer: string): void => {
    const next = [...chosen, ...(other && answer.length > 0 ? [answer] : [])];
    props.setValues(next);
    validate(next.length);
  };
  const choose = (value: string, checked: boolean): void => {
    if (!multiple) {
      setCustom(false);
      commit(checked ? [value] : [], false, customAnswer());
      return;
    }
    commit(
      checked ? [...advertised(), value] : advertised().filter((current) => current !== value),
      custom(),
      customAnswer(),
    );
  };
  const chooseOther = (checked: boolean): void => {
    setCustom(checked);
    commit(multiple ? advertised() : [], checked, customAnswer());
  };

  return (
    <div class="agent-input-choices">
      <For each={props.question.options}>
        {(option, index) => (
          <label class="agent-input-choice">
            <input
              type={multiple ? "checkbox" : "radio"}
              name={group}
              value={option.value}
              checked={props.values.includes(option.value)}
              required={!multiple && props.question.required}
              ref={(input) => {
                if (index() === 0) {
                  counted = input;
                  validate(props.values.length);
                }
              }}
              onChange={(event) => choose(option.value, event.currentTarget.checked)}
            />
            <span>{option.label}</span>
            <Show when={option.description.length > 0}>
              <small>{option.description}</small>
            </Show>
          </label>
        )}
      </For>
      <Show when={!multiple && !props.question.required}>
        <label class="agent-input-choice">
          <input
            type="radio"
            name={group}
            checked={props.values.length === 0 && !custom()}
            onChange={() => {
              setCustom(false);
              commit([], false, customAnswer());
            }}
          />
          <span>No selection</span>
        </label>
      </Show>
      <Show when={props.question.allowsOther}>
        <label class="agent-input-choice">
          <input
            type={multiple ? "checkbox" : "radio"}
            name={group}
            checked={custom()}
            required={!multiple && props.question.required}
            onChange={(event) => chooseOther(event.currentTarget.checked)}
          />
          <span>Other</span>
        </label>
        <Show when={custom()}>
          <input
            type="text"
            required={props.question.required}
            value={customAnswer()}
            placeholder="Type another answer"
            onInput={(event) => {
              setCustomAnswer(event.currentTarget.value);
              commit(multiple ? advertised() : [], true, event.currentTarget.value);
            }}
          />
        </Show>
      </Show>
    </div>
  );
}

function lengthValidity(question: AgentInputQuestion, count: number, noun: string): string {
  const minimum = question.minimumLength ?? 0;
  const maximum = question.maximumLength;
  if (count < minimum) {
    return `Enter at least ${minimum} ${noun}${minimum === 1 ? "" : "s"}.`;
  }
  return maximum !== null && count > maximum
    ? `Enter no more than ${maximum} ${noun}${maximum === 1 ? "" : "s"}.`
    : "";
}
