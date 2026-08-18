import type * as monaco from "monaco-editor";

// Monaco's reveal APIs default to ScrollType.Smooth, which `editor.smoothScrolling` turns into a ~125ms
// viewport animation — long enough for anything reading scroll position or visible ranges right after a
// reveal to observe it mid-flight. Only the wheel animates. Type-only import: main-chunk callers must not
// pull Monaco in for one enum value.
export const REVEAL_SCROLL = 1 satisfies monaco.editor.ScrollType.Immediate;
