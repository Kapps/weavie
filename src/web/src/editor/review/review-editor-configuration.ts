import { StandaloneServices } from "@codingame/monaco-vscode-api";
import { deepClone } from "@codingame/monaco-vscode-api/vscode/vs/base/common/objects";
import { ITextResourceConfigurationService } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/services/textResourceConfiguration.service";
import { configuredEditorOptions, type monaco } from "../monaco-setup";
import { reviewEditorOptions } from "./review-editor-options";

/** Resource defaults followed by the same explicit options supplied to a live review widget. */
export function reviewEditorConfiguration(
  model: monaco.editor.ITextModel,
  overrides: monaco.editor.IEditorOptions,
): monaco.editor.IEditorOptions {
  return deepClone({
    ...StandaloneServices.get(
      ITextResourceConfigurationService,
    ).getValue<monaco.editor.IEditorOptions>(model.uri, "editor"),
    ...configuredEditorOptions(),
    ...reviewEditorOptions(),
    ...overrides,
  });
}
