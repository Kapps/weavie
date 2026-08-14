import { createSignal, For, type JSX, Show } from "solid-js";
import type { AgentInputQuestion } from "../bridge";

export function AgentQuestionControl(props: {
  question: AgentInputQuestion;
  values: string[];
  setValues: (values: string[]) => void;
}): JSX.Element {
  const otherValue = (): string => {
    let value = "__weavie_custom_answer__";
    while (props.question.options.some((option) => option.value === value)) {
      value += "_";
    }
    return value;
  };
  const single = (): string => props.values[0] ?? "";
  const isAdvertised = (value: string): boolean =>
    props.question.options.some((option) => option.value === value);
  const initialCustom = props.values.find((value) => !isAdvertised(value)) ?? "";
  const [custom, setCustom] = createSignal(props.question.allowsOther && initialCustom.length > 0);
  const [customAnswer, setCustomAnswer] = createSignal(initialCustom);
  if (props.question.kind === "boolean") {
    return (
      <input
        type="checkbox"
        checked={single() === "true"}
        onChange={(event) => props.setValues([String(event.currentTarget.checked)])}
      />
    );
  }
  if (props.question.kind === "array") {
    if (props.question.options.length === 0) {
      const values = (input: HTMLTextAreaElement): string[] =>
        input.value.split(/\r?\n/).filter((value) => value.length > 0);
      const validate = (input: HTMLTextAreaElement, count: number): void => {
        const minimum = props.question.minimumLength ?? 0;
        const maximum = props.question.maximumLength;
        input.setCustomValidity(
          count < minimum
            ? `Enter at least ${minimum} value${minimum === 1 ? "" : "s"}.`
            : maximum !== null && count > maximum
              ? `Enter no more than ${maximum} value${maximum === 1 ? "" : "s"}.`
              : "",
        );
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
    const advertisedValues = (): string[] => props.values.filter(isAdvertised);
    return (
      <>
        <select
          multiple
          required={(props.question.minimumLength ?? 0) > 0}
          onChange={(event) => {
            const selected = [...event.currentTarget.selectedOptions].map((option) => option.value);
            const includesOther = selected.includes(otherValue());
            setCustom(includesOther);
            props.setValues([
              ...selected.filter((value) => value !== otherValue()),
              ...(includesOther && customAnswer().length > 0 ? [customAnswer()] : []),
            ]);
          }}
        >
          <For each={props.question.options}>
            {(option) => (
              <option value={option.value} selected={props.values.includes(option.value)}>
                {option.label}
              </option>
            )}
          </For>
          <For each={props.question.allowsOther ? [otherValue()] : []}>
            {(value) => (
              <option value={value} selected={custom()}>
                Other
              </option>
            )}
          </For>
        </select>
        <Show when={custom()}>
          <input
            type="text"
            required={props.question.required}
            value={customAnswer()}
            placeholder="Type another answer"
            onInput={(event) => {
              const value = event.currentTarget.value;
              setCustomAnswer(value);
              props.setValues([...advertisedValues(), ...(value.length > 0 ? [value] : [])]);
            }}
          />
        </Show>
      </>
    );
  }
  if (props.question.options.length > 0) {
    return (
      <>
        <select
          required={props.question.required}
          value={custom() ? otherValue() : single()}
          onChange={(event) => {
            const isOther = event.currentTarget.selectedOptions[0]?.dataset.other === "true";
            setCustom(isOther);
            props.setValues(
              isOther || event.currentTarget.value.length === 0 ? [] : [event.currentTarget.value],
            );
          }}
        >
          <option value="" disabled={props.question.required}>
            {props.question.required ? "Choose an option" : "No selection"}
          </option>
          <For each={props.question.options}>
            {(option) => <option value={option.value}>{option.label}</option>}
          </For>
          <For each={props.question.allowsOther ? [otherValue()] : []}>
            {(value) => (
              <option value={value} data-other="true">
                Other
              </option>
            )}
          </For>
        </select>
        <Show when={custom()}>
          <input
            type="text"
            required={props.question.required}
            value={single()}
            placeholder="Type another answer"
            onInput={(event) =>
              props.setValues(
                event.currentTarget.value.length > 0 ? [event.currentTarget.value] : [],
              )
            }
          />
        </Show>
      </>
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
