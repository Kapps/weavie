import type { EditorOptionsSpec } from "../editor-options";

const LINUX_NORMALIZATION = 5;

export function editorWheelOptions(
  options: Pick<EditorOptionsSpec, "mouseWheelScrollSensitivity" | "fastScrollSensitivity">,
  platform: string | undefined,
) {
  return {
    mouseWheelScrollSensitivity:
      options.mouseWheelScrollSensitivity * (platform === "linux" ? LINUX_NORMALIZATION : 1),
    fastScrollSensitivity: options.fastScrollSensitivity,
  };
}
