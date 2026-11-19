// Editor-behavior options (Monaco IEditorOptions) — the editor analogue of fonts.ts: the `editorOptions` device
// setting, built from the typed `editor.*` settings. Consumers read currentEditorOptions() at creation and
// subscribe via onEditorOptionsChanged() for live updates.

import type { EditorOptionsSpec } from "./bridge";
import { deviceSetting } from "./device-settings";
import { DEFAULT_EDITOR_OPTIONS } from "./editor-options-defaults";

export type { EditorOptionsSpec };

declare global {
  interface Window {
    /** Resolved editor options injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_EDITOR_OPTIONS__?: EditorOptionsSpec;
  }
}

const options = deviceSetting(
  "editorOptions",
  "__WEAVIE_EDITOR_OPTIONS__",
  window.__WEAVIE_EDITOR_OPTIONS__,
  DEFAULT_EDITOR_OPTIONS,
);

/** The editor options to use right now — read this when creating the editor. */
export const currentEditorOptions = options.current;

/** Subscribe to live editor-option changes; returns an unsubscribe function. */
export const onEditorOptionsChanged = options.onChanged;
