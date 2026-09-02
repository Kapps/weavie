import type { EditorOptionsSpec } from "./messaging/protocol-types";

// Plain-browser defaults also provide the complete base snapshot for test hosts. The shipped app always gets
// every field from Core's typed EditorSettings payload.
export const DEFAULT_EDITOR_OPTIONS: EditorOptionsSpec = {
  inlayHints: "on",
  minimap: false,
  bracketPairColorization: true,
  smoothScrolling: true,
  cursorSmoothCaretAnimation: "off",
  renderWhitespace: "none",
  scrollBeyondLastLine: true,
  mouseWheelScrollSensitivity: 1,
  fastScrollSensitivity: 5,
  middleClickAutoscroll: true,
  wordWrap: "off",
  lineNumbers: "on",
  cursorBlinking: "blink",
  renderLineHighlight: "line",
  stickyScroll: true,
  fontLigatures: false,
  indentGuides: true,
  hoverDelay: 300,
  suggestExpandDocs: true,
  commentProse: "documentation",
  paneShortcutHints: true,
  videoAutoplay: true,
  gitBlame: "currentLine",
};
