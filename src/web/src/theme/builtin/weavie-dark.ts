// "Weavie Dark" — the built-in default theme. A deep, premium dark scheme: a near-black editor canvas
// (#06080c) framed by a slightly elevated chrome, under bright cool-grey text. Just two saturated accents
// carry meaning — a refined teal for keywords + the UI, and a soft green for strings; types take a cool
// steel-blue and numeric literals a muted lavender, so the palette reads balanced rather than green-washed.
// The greys span a wide brightness range (bright-white functions → light variables → dim params → subdued
// punctuation → faint comments) so syntax reads with strong contrast against the dark canvas. Warm red/
// yellow appear only where they carry meaning (errors, diffs, warnings). It's a normal VS Code color theme
// (spec §5): the same `colors` map drives the editor, the terminal, and Weavie's chrome, so overrides
// (spec §6) and installed Open VSX themes address the exact same keys.

import type { VsCodeColorTheme } from "../vscode-theme";

/** Stable id Monaco/`setTheme` and `theme.active` reference this built-in by. */
export const WEAVIE_DARK_ID = "weavie-dark";

export const WEAVIE_DARK: VsCodeColorTheme = {
  name: "Weavie Dark",
  type: "dark",
  semanticHighlighting: true,
  colors: {
    // ── Editor ────────────────────────────────────────────────────────────────────────────────────
    "editor.background": "#06080c",
    "editor.foreground": "#dde4ee",
    "editorLineNumber.foreground": "#3f4a5a",
    "editorLineNumber.activeForeground": "#9aa5b4",
    "editorCursor.foreground": "#5cc8bd",
    "editor.selectionBackground": "#2c4a52aa",
    "editor.selectionHighlightBackground": "#2c4a5255",
    "editor.inactiveSelectionBackground": "#2c4a5233",
    "editor.wordHighlightBackground": "#4fbfb622",
    "editor.wordHighlightStrongBackground": "#4fbfb633",
    "editor.findMatchBackground": "#4fbfb655",
    "editor.findMatchHighlightBackground": "#4fbfb62e",
    "editor.lineHighlightBackground": "#0e131b66",
    "editor.lineHighlightBorder": "#00000000",
    "editorWhitespace.foreground": "#222a35",
    "editorIndentGuide.background1": "#171e28",
    "editorIndentGuide.activeBackground1": "#4fbfb644",
    "editorBracketMatch.background": "#4fbfb61f",
    "editorBracketMatch.border": "#4fbfb677",
    "editorBracketHighlight.foreground1": "#4fbfb6",
    "editorBracketHighlight.foreground2": "#aebfd6",
    "editorBracketHighlight.foreground3": "#b3a8d4",
    "editorRuler.foreground": "#171e28",
    "editorGutter.addedBackground": "#5a9a6a",
    "editorGutter.modifiedBackground": "#4fbfb6",
    "editorGutter.deletedBackground": "#c47a7a",
    "editorError.foreground": "#d57a7a",
    "editorWarning.foreground": "#c9a36a",
    "editorInfo.foreground": "#4fbfb6",

    // ── Inlay hints (parameter-name / type chips) ───────────────────────────────────────────────────
    // Kept quiet and chip-less so they annotate without competing with the code they sit between.
    "editorInlayHint.foreground": "#6c7a86",
    "editorInlayHint.background": "#00000000",
    "editorInlayHint.typeForeground": "#6c7a86",
    "editorInlayHint.parameterForeground": "#6c7a86",

    // ── Widgets (hover, suggest, find, peek) ────────────────────────────────────────────────────────
    "editorWidget.background": "#0e1117",
    "editorWidget.border": "#212834",
    "editorHoverWidget.background": "#0e1117",
    "editorHoverWidget.border": "#212834",
    "editorSuggestWidget.background": "#0b0e13",
    "editorSuggestWidget.border": "#212834",
    "editorSuggestWidget.selectedBackground": "#161b24",
    "editorSuggestWidget.highlightForeground": "#4fbfb6",
    "peekViewEditor.background": "#080a0e",
    "peekViewResult.background": "#090b10",

    // ── Global / shared ─────────────────────────────────────────────────────────────────────────────
    focusBorder: "#4fbfb6",
    foreground: "#c8d0dc",
    descriptionForeground: "#7e8794",
    errorForeground: "#d57a7a",
    "icon.foreground": "#7e8794",
    "selection.background": "#2c4a52cc",
    "widget.border": "#212834",
    "sash.hoverBorder": "#4fbfb6",

    // ── Chrome surfaces (sidebar / panel / status / title / tabs) ────────────────────────────────────
    // One step lighter than the editor canvas, so the deep editor reads as the focal well it frames.
    "editorGroup.border": "#1a212b",
    "editorGroupHeader.tabsBackground": "#090b10",
    "panel.background": "#090b10",
    "panel.border": "#1a212b",
    "panelTitle.activeForeground": "#dde4ee",
    "sideBar.background": "#090b10",
    "sideBar.foreground": "#c8d0dc",
    "sideBar.border": "#1a212b",
    "sideBarSectionHeader.background": "#090b10",
    "activityBar.background": "#090b10",
    "activityBar.foreground": "#dde4ee",
    "statusBar.background": "#090b10",
    "statusBar.foreground": "#7e8794",
    "statusBar.border": "#1a212b",
    "statusBar.noFolderBackground": "#090b10",
    "statusBarItem.remoteBackground": "#4fbfb6",
    "statusBarItem.remoteForeground": "#04201d",
    "titleBar.activeBackground": "#090b10",
    "titleBar.activeForeground": "#c8d0dc",
    "titleBar.inactiveBackground": "#090b10",
    "titleBar.inactiveForeground": "#5a626f",
    "titleBar.border": "#1a212b",
    "tab.activeBackground": "#06080c",
    "tab.activeForeground": "#eef2f8",
    "tab.inactiveBackground": "#090b10",
    "tab.inactiveForeground": "#5a626f",
    "tab.border": "#1a212b",
    "tab.activeBorderTop": "#4fbfb6",
    "tab.hoverBackground": "#0e1117",

    // ── Controls (buttons, inputs, lists, scrollbar) ────────────────────────────────────────────────
    "button.background": "#4fbfb6",
    "button.foreground": "#04201d",
    "button.hoverBackground": "#5fcabf",
    "button.secondaryBackground": "#161b24",
    "button.secondaryForeground": "#dde4ee",
    "input.background": "#06080c",
    "input.foreground": "#dde4ee",
    "input.border": "#212834",
    "input.placeholderForeground": "#5a626f",
    "inputOption.activeBorder": "#4fbfb6",
    "inputOption.activeBackground": "#4fbfb633",
    "dropdown.background": "#0e1117",
    "dropdown.border": "#212834",
    "list.activeSelectionBackground": "#161b24",
    "list.activeSelectionForeground": "#eef2f8",
    "list.inactiveSelectionBackground": "#12161e",
    "list.hoverBackground": "#12161e",
    "list.highlightForeground": "#4fbfb6",
    "list.focusBackground": "#161b24",
    "scrollbar.shadow": "#00000066",
    "scrollbarSlider.background": "#2a313c80",
    "scrollbarSlider.hoverBackground": "#3a4250aa",
    "scrollbarSlider.activeBackground": "#4a5666cc",
    "badge.background": "#4fbfb6",
    "badge.foreground": "#04201d",
    "progressBar.background": "#4fbfb6",

    // ── Terminal (xterm consumes these) ─────────────────────────────────────────────────────────────
    "terminal.background": "#090b10",
    "terminal.foreground": "#c8d0dc",
    "terminalCursor.foreground": "#5cc8bd",
    "terminal.selectionBackground": "#2c4a5266",
    "terminal.ansiBlack": "#090b10",
    "terminal.ansiRed": "#d57a7a",
    "terminal.ansiGreen": "#a8cd8c",
    "terminal.ansiYellow": "#c9a36a",
    "terminal.ansiBlue": "#6f9fd4",
    "terminal.ansiMagenta": "#b3a8d4",
    "terminal.ansiCyan": "#4fbfb6",
    "terminal.ansiWhite": "#c8d0dc",
    "terminal.ansiBrightBlack": "#5a626f",
    "terminal.ansiBrightRed": "#df9090",
    "terminal.ansiBrightGreen": "#bcdaa3",
    "terminal.ansiBrightYellow": "#d6b37e",
    "terminal.ansiBrightBlue": "#8fb6e0",
    "terminal.ansiBrightMagenta": "#c6baea",
    "terminal.ansiBrightCyan": "#6fcac2",
    "terminal.ansiBrightWhite": "#eef2f8",
  },
  tokenColors: [
    {
      name: "Comment",
      scope: ["comment", "punctuation.definition.comment", "string.comment"],
      settings: { foreground: "#5e6a79", fontStyle: "italic" },
    },
    {
      name: "String",
      scope: ["string", "string.quoted", "punctuation.definition.string"],
      settings: { foreground: "#a8cd8c" },
    },
    {
      name: "String escape / template expression",
      scope: [
        "constant.character.escape",
        "constant.other.placeholder",
        "punctuation.definition.template-expression",
      ],
      settings: { foreground: "#9bc488" },
    },
    { name: "Regexp", scope: ["string.regexp"], settings: { foreground: "#9ec79f" } },
    {
      name: "Number",
      scope: ["constant.numeric", "constant.character", "keyword.other.unit"],
      settings: { foreground: "#b3a8d4" },
    },
    {
      name: "Language constant (true/false/null)",
      scope: [
        "constant.language",
        "constant.language.boolean",
        "constant.language.null",
        "constant.language.undefined",
      ],
      settings: { foreground: "#b3a8d4" },
    },
    {
      name: "Keyword / control",
      scope: [
        "keyword",
        "keyword.control",
        "keyword.operator.new",
        "keyword.operator.expression",
        "keyword.operator.logical",
      ],
      settings: { foreground: "#4fbfb6" },
    },
    {
      name: "Storage / type keyword / modifier",
      scope: ["storage", "storage.type", "storage.modifier", "keyword.declaration"],
      settings: { foreground: "#4fbfb6" },
    },
    {
      name: "Operator / accessor",
      scope: ["keyword.operator", "punctuation.accessor", "punctuation.separator"],
      settings: { foreground: "#7b8597" },
    },
    {
      name: "Type / class / namespace",
      scope: [
        "entity.name.type",
        "entity.name.class",
        "entity.name.namespace",
        "support.type",
        "support.class",
        "entity.other.inherited-class",
      ],
      settings: { foreground: "#aebfd6" },
    },
    {
      name: "Function / method",
      scope: [
        "entity.name.function",
        "support.function",
        "meta.function-call entity.name.function",
        "entity.name.function.member",
      ],
      settings: { foreground: "#eef2f8" },
    },
    {
      name: "Variable",
      scope: ["variable", "meta.variable", "variable.other.readwrite"],
      settings: { foreground: "#cdd5e2" },
    },
    {
      name: "Parameter",
      scope: ["variable.parameter", "meta.parameter"],
      settings: { foreground: "#a6b0be" },
    },
    {
      name: "Property",
      scope: [
        "variable.other.property",
        "variable.other.object.property",
        "support.variable.property",
        "meta.object-literal.key",
      ],
      settings: { foreground: "#cdd5e2" },
    },
    {
      name: "Constant / enum member",
      scope: ["variable.other.constant", "variable.other.enummember"],
      settings: { foreground: "#b3a8d4" },
    },
    {
      name: "Language variable (this / self / super)",
      scope: ["variable.language", "variable.language.this"],
      settings: { foreground: "#4fbfb6", fontStyle: "italic" },
    },
    {
      name: "Tag",
      scope: ["entity.name.tag", "punctuation.definition.tag"],
      settings: { foreground: "#4fbfb6" },
    },
    {
      name: "Tag attribute",
      scope: ["entity.other.attribute-name"],
      settings: { foreground: "#aebfd6" },
    },
    {
      name: "Decorator / annotation",
      scope: [
        "meta.decorator",
        "punctuation.decorator",
        "storage.type.annotation",
        "entity.name.function.decorator",
      ],
      settings: { foreground: "#4fbfb6" },
    },
    {
      name: "JSON key",
      scope: ["support.type.property-name.json", "meta.structure.dictionary.key.json"],
      settings: { foreground: "#aebfd6" },
    },
    {
      name: "Punctuation / braces",
      scope: ["punctuation", "meta.brace"],
      settings: { foreground: "#7b8597" },
    },
    {
      name: "Markup heading",
      scope: ["markup.heading", "entity.name.section"],
      settings: { foreground: "#4fbfb6", fontStyle: "bold" },
    },
    {
      name: "Markup bold",
      scope: ["markup.bold"],
      settings: { foreground: "#eef2f8", fontStyle: "bold" },
    },
    {
      name: "Markup italic",
      scope: ["markup.italic"],
      settings: { foreground: "#cdd5e2", fontStyle: "italic" },
    },
    {
      name: "Markup link",
      scope: ["markup.underline.link", "string.other.link"],
      settings: { foreground: "#4fbfb6" },
    },
    {
      name: "Markup inline / fenced code",
      scope: ["markup.inline.raw", "markup.fenced_code"],
      settings: { foreground: "#a8cd8c" },
    },
    { name: "Markup inserted", scope: ["markup.inserted"], settings: { foreground: "#a8cd8c" } },
    { name: "Markup deleted", scope: ["markup.deleted"], settings: { foreground: "#d57a7a" } },
    { name: "Invalid", scope: ["invalid", "invalid.illegal"], settings: { foreground: "#d57a7a" } },
  ],
  semanticTokenColors: {
    // Types take a cool steel-blue so they read as colored structure — distinct from the identifier greys
    // and from the green strings, without piling more green onto the screen.
    class: "#aebfd6",
    interface: "#aebfd6",
    enum: "#aebfd6",
    struct: "#aebfd6",
    type: "#aebfd6",
    typeParameter: "#a3b4cd",
    // Namespaces are mostly qualifiers — dimmed so the type they qualify stays the eye's anchor.
    namespace: "#8593a8",
    // Literals (numbers, constants, enum members) take a muted lavender — a second cool accent that keeps
    // them legible and apart from types, again without green.
    enumMember: "#b3a8d4",
    // Functions are the brightest tokens on screen, so call sites pop against everything around them.
    function: "#eef2f8",
    method: "#eef2f8",
    macro: "#4fbfb6",
    keyword: "#4fbfb6",
    string: "#a8cd8c",
    number: "#b3a8d4",
    operator: "#7b8597",
    comment: { foreground: "#5e6a79", fontStyle: "italic" },
    variable: "#cdd5e2",
    "variable.readonly": "#b3a8d4",
    parameter: "#a6b0be",
    property: "#cdd5e2",
    "property.readonly": "#cdd5e2",
  },
};
