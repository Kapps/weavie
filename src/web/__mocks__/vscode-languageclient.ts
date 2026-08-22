// The unit tests run in a node environment, where the real package's `vscode` import can't load. This is the one
// stub of the protocol shapes our LSP units reason about: request method ids (verbatim) and the client enums.

const request = (method: string): { method: string } => ({ method });

export const CallHierarchyIncomingCallsRequest = request("callHierarchy/incomingCalls");
export const CallHierarchyOutgoingCallsRequest = request("callHierarchy/outgoingCalls");
export const CallHierarchyPrepareRequest = request("textDocument/prepareCallHierarchy");
export const CodeActionResolveRequest = request("codeAction/resolve");
export const CodeLensRequest = request("textDocument/codeLens");
export const CodeLensResolveRequest = request("codeLens/resolve");
export const DeclarationRequest = request("textDocument/declaration");
export const DefinitionRequest = request("textDocument/definition");
export const DocumentDiagnosticRequest = request("textDocument/diagnostic");
export const DocumentFormattingRequest = request("textDocument/formatting");
export const DocumentHighlightRequest = request("textDocument/documentHighlight");
export const DocumentRangeFormattingRequest = request("textDocument/rangeFormatting");
export const DocumentRangesFormattingRequest = request("textDocument/rangesFormatting");
export const ExecuteCommandRequest = request("workspace/executeCommand");
export const ImplementationRequest = request("textDocument/implementation");
export const ReferencesRequest = request("textDocument/references");
export const TypeDefinitionRequest = request("textDocument/typeDefinition");
export const TypeHierarchyPrepareRequest = request("textDocument/prepareTypeHierarchy");
export const TypeHierarchySubtypesRequest = request("typeHierarchy/subtypes");
export const TypeHierarchySupertypesRequest = request("typeHierarchy/supertypes");
export const WorkspaceSymbolRequest = request("workspace/symbol");

export const CloseAction = { DoNotRestart: 1, Restart: 2 };
export const ErrorAction = { Continue: 1, Shutdown: 2 };
export const State = { Stopped: 1, Running: 2, Starting: 3 };
