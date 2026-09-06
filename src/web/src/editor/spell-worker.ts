import { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import type { IWebWorkerServer } from "@codingame/monaco-vscode-api/vscode/vs/base/common/worker/webWorker";
import {
  type IModelChangedEvent,
  MirrorTextModel,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/model/mirrorTextModel";
import { createOnigScanner, createOnigString, loadWASM } from "vscode-oniguruma";
import wasmUrl from "vscode-oniguruma/release/onig.wasm?url";
import {
  type IGrammar,
  type ITokenizeLineResult,
  parseRawGrammar,
  Registry,
} from "vscode-textmate";
import type { SpellSpan } from "./spell-prose";

export interface GrammarDefinition {
  scope: string;
  language: string | undefined;
  location: string;
  injectTo: string[];
}

export interface GrammarHost {
  $read(location: string): Promise<string>;
}

export interface IdentifierRange {
  line: number;
  startIndex: number;
  endIndex: number;
}

export class ScopedModel extends MirrorTextModel {
  private readonly tokens: ITokenizeLineResult[] = [];

  override onEvents(event: IModelChangedEvent): void {
    super.onEvents(event);
    for (const change of event.changes) {
      this.tokens.length = Math.min(this.tokens.length, change.range.startLineNumber - 1);
    }
  }

  identifiers(grammar: IGrammar, ranges: SpellSpan[]): IdentifierRange[] {
    const result: IdentifierRange[] = [];
    for (const range of ranges) {
      while (this.tokens.length < range.line) {
        this.tokens.push(
          grammar.tokenizeLine(
            this._lines[this.tokens.length]!,
            this.tokens.at(-1)?.ruleStack ?? null,
          ),
        );
      }
      for (const token of this.tokens[range.line - 1]!.tokens) {
        if (token.startIndex >= range.offset + range.text.length) break;
        const startIndex = Math.max(range.offset, token.startIndex);
        const endIndex = Math.min(range.offset + range.text.length, token.endIndex);
        if (
          endIndex > startIndex &&
          isIdentifierScope(token.scopes) &&
          /\p{L}/u.test(range.text.slice(startIndex - range.offset, endIndex - range.offset))
        ) {
          result.push({ line: range.line, startIndex, endIndex });
        }
      }
    }
    return result;
  }
}

function isIdentifierScope(scopes: string[]): boolean {
  return /^(?:(?:source|meta)\.|(?:entity\.name\.(?:type|class|struct|enum|interface|namespace|function|method|variable|constant|label)|variable\.(?:other|parameter|object))(?:\.|$))/.test(
    scopes.at(-1) ?? "",
  );
}

export class SpellWorker {
  readonly _requestHandlerBrand = undefined;
  private model!: ScopedModel;
  private grammar!: Promise<IGrammar | null>;

  constructor(private readonly server: IWebWorkerServer) {}

  async $init(
    definitions: GrammarDefinition[],
    language: string,
    uri: string,
    lines: string[],
    eol: string,
    version: number,
  ): Promise<void> {
    this.model = new ScopedModel(URI.parse(uri), lines, eol, version);
    const host = this.server.getChannel<GrammarHost>("grammars");
    const registry = new Registry({
      onigLib: fetch(wasmUrl)
        .then((response) => response.arrayBuffer())
        .then(async (bytes) => {
          await loadWASM(bytes);
          return { createOnigScanner, createOnigString };
        }),
      loadGrammar: async (scope) => {
        const definition = definitions.find((item) => item.scope === scope);
        return definition === undefined
          ? null
          : parseRawGrammar(await host.$read(definition.location), definition.location);
      },
      getInjections: (scope) =>
        definitions
          .filter((item) =>
            item.injectTo.some((target) => scope === target || scope.startsWith(`${target}.`)),
          )
          .map((item) => item.scope),
    });
    const definition = definitions.find((item) => item.language === language);
    this.grammar =
      definition === undefined ? Promise.resolve(null) : registry.loadGrammar(definition.scope);
    await this.grammar;
  }

  $update(event: IModelChangedEvent): void {
    this.model.onEvents(event);
  }

  async $tokens(version: number, ranges: SpellSpan[]): Promise<IdentifierRange[]> {
    const grammar = await this.grammar;
    return grammar === null || this.model.version !== version
      ? []
      : this.model.identifiers(grammar, ranges);
  }
}
