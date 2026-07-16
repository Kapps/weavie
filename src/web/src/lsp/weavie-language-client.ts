import { MonacoLanguageClient, type MonacoLanguageClientOptions } from "monaco-languageclient";
import {
  type CancellationToken,
  CodeLensRequest,
  CodeLensResolveRequest,
  DocumentHighlightRequest,
  type MessageSignature,
} from "vscode-languageclient";

const passiveRequestMethods = new Set<string>([
  CodeLensRequest.method,
  CodeLensResolveRequest.method,
  DocumentHighlightRequest.method,
]);

class WeavieLanguageClient extends MonacoLanguageClient {
  override handleFailedRequest<T>(
    type: MessageSignature,
    token: CancellationToken | undefined,
    error: unknown,
    defaultValue: T,
    showNotification?: boolean,
  ): T {
    return super.handleFailedRequest(
      type,
      token,
      error,
      defaultValue,
      showNotification !== false && !passiveRequestMethods.has(type.method),
    );
  }
}

/** Creates a language client that keeps automatic editor-provider failures out of the toast stack. */
export function createWeavieLanguageClient(
  options: MonacoLanguageClientOptions,
): MonacoLanguageClient {
  return new WeavieLanguageClient(options);
}
