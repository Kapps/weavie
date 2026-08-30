import type { URI } from "@codingame/monaco-vscode-api/vscode/vs/base/common/uri";
import { MonacoLanguageClient, type MonacoLanguageClientOptions } from "monaco-languageclient";
import type { DocumentFilter, DocumentSelector, RelativePattern } from "vscode";
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
import { SESSION_FILE_SCHEME } from "../editor/session-uri-scheme";
import { notify } from "../notify/notify";
import { describeError, isCancellation } from "./lsp-errors";
import {
  SessionCommandScope,
  SessionExecuteCommandFeature,
} from "./session-execute-command-feature";

type UnscopedLanguageClientOptions = Omit<MonacoLanguageClientOptions, "clientOptions"> & {
  clientOptions: Omit<MonacoLanguageClientOptions["clientOptions"], "commandIdConverters">;
};

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
  private scopedProtocol2CodeConverter: MonacoLanguageClient["protocol2CodeConverter"] | undefined;

  constructor(
    options: MonacoLanguageClientOptions,
    commandScope: SessionCommandScope,
    modelWorkspaceUri: URI,
  ) {
    super(options);
    const baseConverter = super.protocol2CodeConverter;
    this.scopedProtocol2CodeConverter = {
      ...baseConverter,
      asDocumentSelector: (selector) =>
        scopeDocumentSelector(baseConverter.asDocumentSelector(selector), modelWorkspaceUri),
    };
    super.registerFeature(new SessionExecuteCommandFeature(this, commandScope));
  }

  override get protocol2CodeConverter(): MonacoLanguageClient["protocol2CodeConverter"] {
    return this.scopedProtocol2CodeConverter ?? super.protocol2CodeConverter;
  }

  override registerFeature(feature: Parameters<MonacoLanguageClient["registerFeature"]>[0]): void {
    if (
      "registrationType" in feature &&
      feature.registrationType.method === ExecuteCommandRequest.method
    ) {
      return;
    }
    super.registerFeature(feature);
  }

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

function scopeDocumentSelector(
  selector: DocumentSelector,
  modelWorkspaceUri: URI,
): DocumentSelector {
  const filters = Array.isArray(selector) ? selector : [selector];
  return filters.flatMap((filter): DocumentFilter[] => {
    if (typeof filter === "string") {
      return [scopedFilter(filter, undefined, modelWorkspaceUri)];
    }
    if (
      filter.notebookType !== undefined ||
      (filter.scheme !== undefined && filter.scheme !== "file" && filter.scheme !== "*")
    ) {
      return [];
    }
    return [
      scopedFilter(
        filter.language,
        typeof filter.pattern === "string" ? filter.pattern : filter.pattern?.pattern,
        modelWorkspaceUri,
      ),
    ];
  });
}

function scopedFilter(
  language: string | undefined,
  pattern: string | undefined,
  modelWorkspaceUri: URI,
): DocumentFilter {
  const relativePattern: RelativePattern = {
    base: modelWorkspaceUri.fsPath,
    baseUri: modelWorkspaceUri,
    pattern: pattern ?? "**",
  };
  return {
    ...(language === undefined ? {} : { language }),
    scheme: SESSION_FILE_SCHEME,
    pattern: relativePattern,
  };
}

/** Creates a language client that toasts only the failures of requests the user invoked; the rest are logged. */
export function createWeavieLanguageClient(
  options: UnscopedLanguageClientOptions,
  commandNamespace: string,
  modelWorkspaceUri: URI,
): MonacoLanguageClient {
  const commandScope = new SessionCommandScope(commandNamespace);
  return new WeavieLanguageClient(
    {
      ...options,
      clientOptions: {
        ...options.clientOptions,
        commandIdConverters: commandScope.converters,
      },
    },
    commandScope,
    modelWorkspaceUri,
  );
}
