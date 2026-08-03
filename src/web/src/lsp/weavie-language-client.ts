import { MonacoLanguageClient, type MonacoLanguageClientOptions } from "monaco-languageclient";
import {
  type CancellationToken,
  CodeLensRequest,
  CodeLensResolveRequest,
  DocumentDiagnosticRequest,
  DocumentHighlightRequest,
  type MessageSignature,
  State,
} from "vscode-languageclient";

const passiveRequestMethods = new Set<string>([
  CodeLensRequest.method,
  CodeLensResolveRequest.method,
  DocumentDiagnosticRequest.method,
  DocumentHighlightRequest.method,
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
