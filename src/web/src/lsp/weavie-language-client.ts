import { MonacoLanguageClient, type MonacoLanguageClientOptions } from "monaco-languageclient";
import {
  CallHierarchyIncomingCallsRequest,
  CallHierarchyOutgoingCallsRequest,
  CallHierarchyPrepareRequest,
  type CancellationToken,
  CodeActionResolveRequest,
  DeclarationRequest,
  DefinitionRequest,
  DocumentFormattingRequest,
  DocumentRangeFormattingRequest,
  DocumentRangesFormattingRequest,
  ExecuteCommandRequest,
  ImplementationRequest,
  type MessageSignature,
  ReferencesRequest,
  State,
  TypeDefinitionRequest,
  TypeHierarchyPrepareRequest,
  TypeHierarchySubtypesRequest,
  TypeHierarchySupertypesRequest,
  WorkspaceSymbolRequest,
} from "vscode-languageclient";
import { notify } from "../notify/notify";
import { describeError, isCancellation } from "./lsp-errors";

// The requests a user invokes and waits on a result from, mapped to the action's user-facing name. Everything
// else (highlighting, lenses, diagnostics, completion) is background enrichment the editor re-runs constantly,
// where a server that won't answer — a file past its size limit, say — is noise, so those only reach the log.
const userInvokedRequests = new Map<string, string>([
  [DefinitionRequest.method, "Go to Definition"],
  [DeclarationRequest.method, "Go to Declaration"],
  [TypeDefinitionRequest.method, "Go to Type Definition"],
  [ImplementationRequest.method, "Go to Implementation"],
  [ReferencesRequest.method, "Find All References"],
  [WorkspaceSymbolRequest.method, "Go to Symbol in Workspace"],
  [CallHierarchyPrepareRequest.method, "Call Hierarchy"],
  [CallHierarchyIncomingCallsRequest.method, "Incoming Calls"],
  [CallHierarchyOutgoingCallsRequest.method, "Outgoing Calls"],
  [TypeHierarchyPrepareRequest.method, "Type Hierarchy"],
  [TypeHierarchySupertypesRequest.method, "Supertypes"],
  [TypeHierarchySubtypesRequest.method, "Subtypes"],
  [DocumentFormattingRequest.method, "Format Document"],
  [DocumentRangeFormattingRequest.method, "Format Selection"],
  [DocumentRangesFormattingRequest.method, "Format Selection"],
  [CodeActionResolveRequest.method, "Code action"],
  [ExecuteCommandRequest.method, "Command"],
]);

class WeavieLanguageClient extends MonacoLanguageClient {
  override stop(...args: [] | [number]): Promise<void> {
    // Upstream calls stop() while an initialization failure is still Starting, where its own stop throws.
    // The closing transport cleans a failed start; only a fully running client needs protocol shutdown.
    if (this.state !== State.Running) {
      return Promise.resolve();
    }
    return args.length === 0 ? super.stop() : super.stop(args[0]);
  }

  override error(message: string, data?: unknown, _showNotification?: boolean | "force"): void {
    // The client's own reports ("Server initialization failed.", one per reconnect attempt) would become sticky
    // error toasts duplicating what the pool already says about that server. Log them; the pool notifies.
    super.error(message, data, false);
  }

  override handleFailedRequest<T>(
    type: MessageSignature,
    token: CancellationToken | undefined,
    error: unknown,
    defaultValue: T,
    showNotification?: boolean,
  ): T {
    // Only a rethrow is a real failure — a `super` that returns swallowed it (a dead connection, modified
    // content). The toast is keyed by method so a provider that re-fires (ctrl-hover sweeps definition on every
    // mouse move) replaces its warning in place instead of stacking a column of them.
    try {
      return super.handleFailedRequest(type, token, error, defaultValue, showNotification);
    } catch (failure) {
      const action = userInvokedRequests.get(type.method);
      if (action !== undefined && showNotification !== false && !isCancellation(failure)) {
        notify("warn", `${action} failed: ${describeError(failure)}`, `lsp:${type.method}`);
      }
      throw failure;
    }
  }
}

/** Creates a language client that toasts only the failures of requests the user invoked; the rest are logged. */
export function createWeavieLanguageClient(
  options: MonacoLanguageClientOptions,
): MonacoLanguageClient {
  return new WeavieLanguageClient(options);
}
