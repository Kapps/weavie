import * as monaco from "monaco-editor";
import type { MonacoLanguageClient } from "monaco-languageclient";
import {
  type ClientCapabilities,
  type DocumentSelector,
  type DynamicFeature,
  type ExecuteCommandRegistrationOptions,
  ExecuteCommandRequest,
  type FeatureState,
  type LanguageClientOptions,
  type RegistrationData,
  type ServerCapabilities,
} from "vscode-languageclient";

/** Carries a server command's producing client through Monaco and back onto the wire. */
export class SessionCommandScope {
  readonly converters: LanguageClientOptions["commandIdConverters"] = {
    protocol2Code: (command) => this.aliases.get(command) ?? command,
    code2Protocol: (command) => this.rawByAlias.get(command) ?? command,
  };
  readonly staticRegistrationId: string;
  private readonly aliases = new Map<string, string>();
  private readonly rawByAlias = new Map<string, string>();

  constructor(namespace: string) {
    const prefix = `weavie.lsp.command.${namespace.length}.${namespace}`;
    this.staticRegistrationId = `${prefix}.static`;
    this.prefix = prefix;
  }

  private readonly prefix: string;

  advertise(command: string): string {
    const existing = this.aliases.get(command);
    if (existing !== undefined) {
      return existing;
    }
    const alias = `${this.prefix}.${this.aliases.size + 1}`;
    this.aliases.set(command, alias);
    this.rawByAlias.set(alias, command);
    return alias;
  }
}

interface CommandHandler {
  references: number;
  disposable: monaco.IDisposable;
}

/** Registers client-unique aliases that close over the exact language client which advertised them. */
export class SessionExecuteCommandFeature
  implements DynamicFeature<ExecuteCommandRegistrationOptions>
{
  readonly registrationType = ExecuteCommandRequest.type;
  private readonly registrations = new Map<string, string[]>();
  private readonly handlers = new Map<string, CommandHandler>();

  constructor(
    private readonly client: MonacoLanguageClient,
    private readonly scope: SessionCommandScope,
  ) {}

  getState(): FeatureState {
    return {
      kind: "workspace",
      id: this.registrationType.method,
      registrations: this.registrations.size > 0,
    };
  }

  fillClientCapabilities(capabilities: ClientCapabilities): void {
    capabilities.workspace ??= {};
    capabilities.workspace.executeCommand ??= {};
    capabilities.workspace.executeCommand.dynamicRegistration = true;
  }

  initialize(
    capabilities: ServerCapabilities,
    _documentSelector: DocumentSelector | undefined,
  ): void {
    if (capabilities.executeCommandProvider === undefined) {
      return;
    }
    this.register({
      id: this.scope.staticRegistrationId,
      registerOptions: capabilities.executeCommandProvider,
    });
  }

  register(data: RegistrationData<ExecuteCommandRegistrationOptions>): void {
    this.unregister(data.id);
    const registered = [...new Set(data.registerOptions.commands)];
    this.registrations.set(data.id, registered);
    for (const command of registered) {
      const handler = this.handlers.get(command);
      if (handler !== undefined) {
        handler.references += 1;
        continue;
      }
      const alias = this.scope.advertise(command);
      this.handlers.set(command, {
        references: 1,
        disposable: monaco.editor.registerCommand(alias, (_accessor, ...args) =>
          this.execute(command, args),
        ),
      });
    }
  }

  unregister(id: string): void {
    const registered = this.registrations.get(id);
    if (registered === undefined) {
      return;
    }
    this.registrations.delete(id);
    for (const command of registered) {
      const handler = this.handlers.get(command);
      if (handler?.references === 1) {
        handler.disposable.dispose();
        this.handlers.delete(command);
      } else if (handler !== undefined) {
        handler.references -= 1;
      }
    }
  }

  clear(): void {
    for (const handler of this.handlers.values()) {
      handler.disposable.dispose();
    }
    this.handlers.clear();
    this.registrations.clear();
  }

  private execute(command: string, args: unknown[]): unknown {
    const execute = (requestedCommand: string, requestedArgs: unknown[]) =>
      this.client
        .sendRequest(ExecuteCommandRequest.type, {
          command: requestedCommand,
          arguments: requestedArgs,
        })
        .then(undefined, (error: unknown) =>
          this.client.handleFailedRequest(ExecuteCommandRequest.type, undefined, error, undefined),
        );
    return this.client.middleware.executeCommand
      ? this.client.middleware.executeCommand(command, args, execute)
      : execute(command, args);
  }
}
