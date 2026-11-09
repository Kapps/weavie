import type { JSX } from "solid-js";
import { ControlMenu } from "./ControlMenu";
import { ModalShell } from "./ModalShell";
import type { ChoicePickerRequest } from "./picker-store";
import "./choice-picker.css";

/** A host-asked choice as a modal listbox: arrows move, Enter picks, Esc or a click outside dismisses, and a window blur does not. */
export function ChoicePicker(props: {
  picker: ChoicePickerRequest;
  onPick: (id: string) => void;
  onClose: () => void;
}): JSX.Element {
  return (
    <ModalShell labelledBy="choice-picker-title" onDismiss={props.onClose} class="choice-picker">
      <div class="confirm-title" id="choice-picker-title">
        {props.picker.title}
      </div>
      <ControlMenu
        axis={{
          id: "choice",
          label: props.picker.title,
          description: null,
          category: null,
          kind: "select",
          value: props.picker.value,
          valueLabel: "",
          options: props.picker.options,
        }}
        class="choice-picker-menu"
        inside={null}
        onPick={props.onPick}
        onClose={props.onClose}
      />
    </ModalShell>
  );
}
