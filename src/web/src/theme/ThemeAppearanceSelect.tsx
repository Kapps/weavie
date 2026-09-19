import type { ThemeFilter, ThemePickerModel } from "./picker-model";

export function ThemeAppearanceSelect(props: { model: ThemePickerModel }) {
  return (
    <label class="theme-picker-sort">
      Appearance
      <select
        aria-label="Theme appearance"
        value={props.model.mode()}
        disabled={props.model.saving()}
        onChange={(event) => props.model.setMode(event.currentTarget.value as ThemeFilter)}
      >
        <option value="light">Light</option>
        <option value="dark">Dark</option>
        <option value="all">All themes</option>
      </select>
    </label>
  );
}
