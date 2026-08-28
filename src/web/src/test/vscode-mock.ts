export class Position {
  constructor(
    readonly line: number,
    readonly character: number,
  ) {}
}

export class Range {
  constructor(
    readonly start: Position,
    readonly end: Position,
  ) {}
}

export class CompletionItem {
  command?: unknown;

  constructor(readonly label: string) {}
}

export class CodeAction {
  command?: unknown;

  constructor(readonly title: string) {}
}

export class CodeLens {
  command?: unknown;

  constructor(readonly range: Range) {}
}

export class InlayHintLabelPart {
  command?: unknown;

  constructor(readonly value: string) {}
}

export class InlayHint {
  constructor(
    readonly position: Position,
    readonly label: string | InlayHintLabelPart[],
  ) {}
}

export class InlineCompletionItem {
  constructor(
    readonly insertText: string,
    readonly range: Range,
    readonly command: unknown,
  ) {}
}

export class InlineCompletionList {
  constructor(readonly items: InlineCompletionItem[]) {}
}

export class Uri {}
export class SnippetString {}
export class DocumentLink {}
export class Diagnostic {}
export class CallHierarchyItem {}
export class TypeHierarchyItem {}
export class SymbolInformation {}

export const CodeActionKind = {
  Empty: "",
  QuickFix: "quickfix",
  Refactor: "refactor",
  RefactorExtract: "refactor.extract",
  RefactorInline: "refactor.inline",
  RefactorRewrite: "refactor.rewrite",
  Source: "source",
  SourceOrganizeImports: "source.organizeImports",
};
