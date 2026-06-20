// "Weavie Light" — the Paper White companion to Weavie Dark. A green-forward base (teal-green keywords and
// strings, deep sea-green numbers) on a near-white canvas, with the identifier family split by HUE so roles
// never blur: variables a muted denim blue (italic), properties a lighter blue cousin one step up, functions
// a confident crimson — the lone warm accent — and types a cool cyan-teal. Comments recede to a soft neutral
// grey and punctuation is lightened so it stays out of the way. Chrome sits one step *darker* than the editor
// (the mirror of Dark, where it sits one step lighter than black), so the canvas reads as the focal well it
// frames. Same `colors` map drives editor, terminal, and chrome (spec §5), so overrides (spec §6) and
// installed Open VSX themes address the exact same keys.

import type { VsCodeColorTheme } from "../vscode-theme";

/** Stable id Monaco/`setTheme` and the `theme.light`/`theme.dark` settings reference this built-in by. */
export const WEAVIE_LIGHT_ID = "weavie-light";

export const WEAVIE_LIGHT: VsCodeColorTheme = {
  name: "Weavie Light",
  type: "light",
  semanticHighlighting: true,
  colors: {
    // ── Editor ────────────────────────────────────────────────────────────────────────────────────
    "editor.background": "#ffffff",
    "editor.foreground": "#1f2933",
    "editorLineNumber.foreground": "#c2cad3",
    "editorLineNumber.activeForeground": "#4b5563",
    "editorCursor.foreground": "#1f9d78",
    "editor.selectionBackground": "#1f9d7833",
    "editor.selectionHighlightBackground": "#1f9d781f",
    "editor.inactiveSelectionBackground": "#1f9d7817",
    "editor.wordHighlightBackground": "#1f9d7820",
    "editor.wordHighlightStrongBackground": "#1f9d7830",
    "editor.findMatchBackground": "#1f9d7855",
    "editor.findMatchHighlightBackground": "#1f9d7826",
    "editor.lineHighlightBackground": "#1f29330a",
    "editor.lineHighlightBorder": "#00000000",
    "editorWhitespace.foreground": "#dfe3e8",
    "editorIndentGuide.background1": "#ecedf0",
    "editorIndentGuide.activeBackground1": "#1f9d7855",
    "editorBracketMatch.background": "#1f9d781f",
    "editorBracketMatch.border": "#1f9d7777",
    "editorBracketHighlight.foreground1": "#1f9d78",
    "editorBracketHighlight.foreground2": "#0e8398",
    "editorBracketHighlight.foreground3": "#6b7682",
    "editorRuler.foreground": "#eceef1",
    "editorGutter.addedBackground": "#3f9d5a",
    "editorGutter.modifiedBackground": "#1f9d78",
    "editorGutter.deletedBackground": "#c4666a",
    "editorError.foreground": "#c8413b",
    "editorWarning.foreground": "#b5701a",
    "editorInfo.foreground": "#1f9d78",

    // ── Inlay hints (parameter-name / type chips) ───────────────────────────────────────────────────
    // Kept quiet and chip-less so they annotate without competing with the code they sit between.
    "editorInlayHint.foreground": "#9aa4b0",
    "editorInlayHint.background": "#00000000",
    "editorInlayHint.typeForeground": "#9aa4b0",
    "editorInlayHint.parameterForeground": "#9aa4b0",

    // ── Widgets (hover, suggest, find, peek) ────────────────────────────────────────────────────────
    "editorWidget.background": "#f4f6f8",
    "editorWidget.border": "#dfe3e8",
    "editorHoverWidget.background": "#f7f9fb",
    "editorHoverWidget.border": "#dfe3e8",
    "editorSuggestWidget.background": "#ffffff",
    "editorSuggestWidget.border": "#dfe3e8",
    "editorSuggestWidget.selectedBackground": "#eef1f4",
    "editorSuggestWidget.highlightForeground": "#1f9d78",
    "peekViewEditor.background": "#fbfcfd",
    "peekViewResult.background": "#f4f6f8",

    // ── Global / shared ─────────────────────────────────────────────────────────────────────────────
    focusBorder: "#1f9d78",
    foreground: "#2b3640",
    descriptionForeground: "#6f7884",
    errorForeground: "#c8413b",
    "icon.foreground": "#6b7682",
    "selection.background": "#1f9d7833",
    "widget.border": "#dfe3e8",
    "sash.hoverBorder": "#1f9d78",

    // ── Chrome surfaces (sidebar / panel / status / title / tabs) ────────────────────────────────────
    // One step darker than the pure-white editor, so the canvas reads as the focal well it frames.
    "editorGroup.border": "#e3e7ec",
    "editorGroupHeader.tabsBackground": "#f4f6f8",
    "panel.background": "#f4f6f8",
    "panel.border": "#e3e7ec",
    "panelTitle.activeForeground": "#1f2933",
    "sideBar.background": "#f4f6f8",
    "sideBar.foreground": "#34404b",
    "sideBar.border": "#e3e7ec",
    "sideBarSectionHeader.background": "#f4f6f8",
    "activityBar.background": "#f4f6f8",
    "activityBar.foreground": "#1f2933",
    "statusBar.background": "#f4f6f8",
    "statusBar.foreground": "#6f7884",
    "statusBar.border": "#e3e7ec",
    "statusBar.noFolderBackground": "#f4f6f8",
    "statusBarItem.remoteBackground": "#1f9d78",
    "statusBarItem.remoteForeground": "#ffffff",
    "titleBar.activeBackground": "#f4f6f8",
    "titleBar.activeForeground": "#2b3640",
    "titleBar.inactiveBackground": "#f4f6f8",
    "titleBar.inactiveForeground": "#9aa4b0",
    "titleBar.border": "#e3e7ec",
    "tab.activeBackground": "#ffffff",
    "tab.activeForeground": "#11181f",
    "tab.inactiveBackground": "#f4f6f8",
    "tab.inactiveForeground": "#79828f",
    "tab.border": "#e3e7ec",
    "tab.activeBorderTop": "#1f9d78",
    "tab.hoverBackground": "#eef1f4",

    // ── Controls (buttons, inputs, lists, scrollbar) ────────────────────────────────────────────────
    "button.background": "#1f9d78",
    "button.foreground": "#ffffff",
    "button.hoverBackground": "#1c8e6c",
    "button.secondaryBackground": "#eaeef2",
    "button.secondaryForeground": "#2b3640",
    "input.background": "#ffffff",
    "input.foreground": "#1f2933",
    "input.border": "#d8dee5",
    "input.placeholderForeground": "#9aa4b0",
    "inputOption.activeBorder": "#1f9d78",
    "inputOption.activeBackground": "#1f9d7826",
    "dropdown.background": "#ffffff",
    "dropdown.border": "#d8dee5",
    "list.activeSelectionBackground": "#e6efeb",
    "list.activeSelectionForeground": "#11181f",
    "list.inactiveSelectionBackground": "#eef1f4",
    "list.hoverBackground": "#eef1f4",
    "list.highlightForeground": "#1f9d78",
    "list.focusBackground": "#e6efeb",
    "scrollbar.shadow": "#00000018",
    "scrollbarSlider.background": "#1f29332e",
    "scrollbarSlider.hoverBackground": "#1f293344",
    "scrollbarSlider.activeBackground": "#1f293359",
    "badge.background": "#1f9d78",
    "badge.foreground": "#ffffff",
    "progressBar.background": "#1f9d78",

    // ── Terminal (xterm consumes these) ─────────────────────────────────────────────────────────────
    "terminal.background": "#fbfcfd",
    "terminal.foreground": "#1f2933",
    "terminalCursor.foreground": "#1f9d78",
    "terminal.selectionBackground": "#1f9d7833",
    "terminal.ansiBlack": "#2b3640",
    "terminal.ansiRed": "#c8413b",
    "terminal.ansiGreen": "#2e8b3d",
    "terminal.ansiYellow": "#b06a00",
    "terminal.ansiBlue": "#2f6fb0",
    "terminal.ansiMagenta": "#9356c4",
    "terminal.ansiCyan": "#157f78",
    "terminal.ansiWhite": "#6b7682",
    "terminal.ansiBrightBlack": "#9aa4b0",
    "terminal.ansiBrightRed": "#d85650",
    "terminal.ansiBrightGreen": "#3fa050",
    "terminal.ansiBrightYellow": "#c07f13",
    "terminal.ansiBrightBlue": "#3f80c0",
    "terminal.ansiBrightMagenta": "#a468d0",
    "terminal.ansiBrightCyan": "#1f9d8f",
    "terminal.ansiBrightWhite": "#11181f",
  },
  tokenColors: [
    {
      name: "Comment",
      scope: ["comment", "punctuation.definition.comment", "string.comment"],
      settings: { foreground: "#9aa6ad", fontStyle: "italic" },
    },
    {
      name: "String",
      scope: ["string", "string.quoted", "punctuation.definition.string"],
      settings: { foreground: "#2e8b3d" },
    },
    {
      name: "String escape / template expression",
      scope: [
        "constant.character.escape",
        "constant.other.placeholder",
        "punctuation.definition.template-expression",
      ],
      settings: { foreground: "#279a44" },
    },
    { name: "Regexp", scope: ["string.regexp"], settings: { foreground: "#279a44" } },
    {
      name: "Number",
      scope: ["constant.numeric", "constant.character", "keyword.other.unit"],
      settings: { foreground: "#13836b" },
    },
    {
      name: "Language constant (true/false/null)",
      scope: [
        "constant.language",
        "constant.language.boolean",
        "constant.language.null",
        "constant.language.undefined",
      ],
      settings: { foreground: "#13836b" },
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
      settings: { foreground: "#1f8a73" },
    },
    {
      name: "Storage / type keyword / modifier",
      scope: ["storage", "storage.type", "storage.modifier", "keyword.declaration"],
      settings: { foreground: "#1f8a73" },
    },
    {
      name: "Operator / accessor",
      scope: ["keyword.operator", "punctuation.accessor", "punctuation.separator"],
      settings: { foreground: "#8a949e" },
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
      settings: { foreground: "#0e8398" },
    },
    {
      name: "Function / method",
      scope: [
        "entity.name.function",
        "support.function",
        "meta.function-call entity.name.function",
        "entity.name.function.member",
      ],
      settings: { foreground: "#c0392b" },
    },
    {
      name: "Variable",
      scope: [
        "variable",
        "meta.variable",
        "variable.other.readwrite",
        "variable.parameter",
        "meta.parameter",
      ],
      settings: { foreground: "#3b5a9c", fontStyle: "italic" },
    },
    {
      name: "Property",
      scope: [
        "variable.other.property",
        "variable.other.object.property",
        "support.variable.property",
        "meta.object-literal.key",
      ],
      settings: { foreground: "#6079a8" },
    },
    {
      name: "Constant / enum member",
      scope: ["variable.other.constant", "variable.other.enummember"],
      settings: { foreground: "#13836b" },
    },
    {
      name: "Language variable (this / self / super)",
      scope: ["variable.language", "variable.language.this"],
      settings: { foreground: "#1f8a73", fontStyle: "italic" },
    },
    {
      name: "Tag",
      scope: ["entity.name.tag", "punctuation.definition.tag"],
      settings: { foreground: "#1f8a73" },
    },
    {
      name: "Tag attribute",
      scope: ["entity.other.attribute-name"],
      settings: { foreground: "#6079a8" },
    },
    {
      name: "Decorator / annotation",
      scope: [
        "meta.decorator",
        "punctuation.decorator",
        "storage.type.annotation",
        "entity.name.function.decorator",
      ],
      settings: { foreground: "#0e8398" },
    },
    {
      name: "JSON key",
      scope: ["support.type.property-name.json", "meta.structure.dictionary.key.json"],
      settings: { foreground: "#6079a8" },
    },
    {
      name: "Punctuation / braces",
      scope: ["punctuation", "meta.brace"],
      settings: { foreground: "#aab2ba" },
    },
    {
      name: "Markup heading",
      scope: ["markup.heading", "entity.name.section"],
      settings: { foreground: "#0e8398", fontStyle: "bold" },
    },
    {
      name: "Markup bold",
      scope: ["markup.bold"],
      settings: { foreground: "#11181f", fontStyle: "bold" },
    },
    {
      name: "Markup italic",
      scope: ["markup.italic"],
      settings: { foreground: "#2b3640", fontStyle: "italic" },
    },
    {
      name: "Markup link",
      scope: ["markup.underline.link", "string.other.link"],
      settings: { foreground: "#1f9d78" },
    },
    {
      name: "Markup inline / fenced code",
      scope: ["markup.inline.raw", "markup.fenced_code"],
      settings: { foreground: "#2e8b3d" },
    },
    { name: "Markup inserted", scope: ["markup.inserted"], settings: { foreground: "#2e8b3d" } },
    { name: "Markup deleted", scope: ["markup.deleted"], settings: { foreground: "#c8413b" } },
    { name: "Invalid", scope: ["invalid", "invalid.illegal"], settings: { foreground: "#c8413b" } },
  ],
  semanticTokenColors: {
    // Types take a cool cyan-teal — distinct from the green keywords and the blue identifiers;
    // namespaces are a dimmer teal since they're mostly qualifiers.
    class: "#0e8398",
    interface: "#0e8398",
    enum: "#0e8398",
    struct: "#0e8398",
    type: "#0e8398",
    typeParameter: "#3a9aac",
    namespace: "#3a8590",
    // Literal values (numbers, constants, enum members) share the deep sea-green of numbers.
    enumMember: "#13836b",
    number: "#13836b",
    "variable.readonly": "#13836b",
    // Functions are crimson — the lone warm accent, so calls and definitions stand out from the data.
    function: "#c0392b",
    method: "#c0392b",
    macro: "#1f8a73",
    keyword: "#1f8a73",
    string: "#2e8b3d",
    operator: "#8a949e",
    comment: { foreground: "#9aa6ad", fontStyle: "italic" },
    // Variables (incl. parameters) are a denim blue, italic — the data flowing through; properties a lighter
    // blue cousin, one step up, so members read as related-but-distinct.
    variable: { foreground: "#3b5a9c", fontStyle: "italic" },
    parameter: { foreground: "#3b5a9c", fontStyle: "italic" },
    property: "#6079a8",
    "property.readonly": "#6079a8",
  },
};
