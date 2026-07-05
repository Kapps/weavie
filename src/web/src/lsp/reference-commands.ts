// Bridge the LSP "N Reference(s)" CodeLens to Monaco's references peek: its command id isn't wired to a handler
// that accepts LSP-shaped args, so without this the click silently no-ops (unlike right-click / ctrl-click).

import { ICommandService } from "@codingame/monaco-vscode-api/services";
import * as monaco from "monaco-editor";
import { log } from "../bridge";

// LSP positions/ranges are 0-based line/character; Monaco's are 1-based lineNumber/column.
interface LspPosition {
  line: number;
  character: number;
}

interface LspLocation {
  uri: string;
  range: { start: LspPosition; end: LspPosition };
}

// csharp-ls: the lens command's sole argument is a serialized ReferenceParams.
interface ReferenceParams {
  textDocument: { uri: string };
  position: LspPosition;
}

let installed = false;

/** Registers the reference-CodeLens command bridges once (idempotent across hot reloads). */
export function installReferenceCommands(): void {
  if (installed) {
    return;
  }
  installed = true;

  // csharp-ls uses the raw LSP method id `textDocument/references` (no Monaco command) and ships no locations,
  // so re-run the references query at the lens position — exactly what right-click "Find All References" does.
  monaco.editor.registerCommand("textDocument/references", (_accessor, arg: ReferenceParams) => {
    const uri = arg.textDocument.uri;
    const editor = editorShowing(uri);
    if (editor === undefined) {
      // The lens lives in the editor showing its file, so this is unreachable — but log rather than silently
      // re-hide the very "click does nothing" bug this fixes, should a uri ever normalize differently.
      log("warn", `reference lens: no open editor for ${uri}`);
      return;
    }
    editor.setPosition(toPosition(arg.position));
    editor.focus();
    editor.trigger("weavie-reference-lens", "editor.action.goToReferences", null);
  });

  // The TypeScript/generic ecosystem uses `editor.action.showReferences` and peeks the [uri, position, locations]
  // it's handed — a references OR implementations lens — so forward those exact locations, converted to Monaco
  // types, to the built-in peek (which resolves the editor itself). Ours wins over Monaco's alias for that id as
  // the later registration; the alias only rejects the LSP-shaped args.
  monaco.editor.registerCommand(
    "editor.action.showReferences",
    (accessor, uri: string, position: LspPosition, locations: LspLocation[]) => {
      void accessor
        .get(ICommandService)
        .executeCommand(
          "editor.action.peekLocations",
          monaco.Uri.parse(uri),
          toPosition(position),
          locations.map(toLocation),
        );
    },
  );
}

// The editor whose model is `uri` — the lens's own file, always open when its lens is clicked.
function editorShowing(uri: string): monaco.editor.ICodeEditor | undefined {
  const target = monaco.Uri.parse(uri).toString();
  return monaco.editor.getEditors().find((e) => e.getModel()?.uri.toString() === target);
}

function toPosition(p: LspPosition): monaco.IPosition {
  return { lineNumber: p.line + 1, column: p.character + 1 };
}

function toLocation(loc: LspLocation): monaco.languages.Location {
  return {
    uri: monaco.Uri.parse(loc.uri),
    range: {
      startLineNumber: loc.range.start.line + 1,
      startColumn: loc.range.start.character + 1,
      endLineNumber: loc.range.end.line + 1,
      endColumn: loc.range.end.character + 1,
    },
  };
}
