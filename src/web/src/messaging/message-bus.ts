import type { MessageEnvelope, MessageScope, SessionAddress } from "./message-envelope";
import { PAGE_EPOCH } from "./page-epoch";

type EventHandler<T> = (payload: T, signal: AbortSignal) => void | Promise<void>;
type RequestHandler<TRequest, TResponse> = (
  payload: TRequest,
  signal: AbortSignal,
) => TResponse | Promise<TResponse>;

interface PendingRequest {
  feature: string;
  name: string;
  resolve: (payload: unknown) => void;
  reject: (error: Error) => void;
  cleanup: () => void;
}

interface RegisteredRequest {
  run: RequestHandler<unknown, unknown>;
  concurrent: boolean;
}

interface IncomingRequest {
  feature: string;
  name: string;
  cancellation: AbortController;
}

const addressEquals = (left: SessionAddress | null, right: SessionAddress | null): boolean =>
  left === null || right === null
    ? left === right
    : left.slot === right.slot && left.incarnation === right.incarnation;

const keyOf = (feature: string, name: string): string => `${feature}\0${name}`;
let busSequence = 0;

export class MessageBus {
  private readonly events = new Map<string, Set<EventHandler<unknown>>>();
  private readonly requests = new Map<string, RegisteredRequest>();
  private readonly pending = new Map<string, PendingRequest>();
  private readonly incoming = new Map<string, IncomingRequest>();
  private readonly lanes = new Map<string, Promise<void>>();
  private readonly lifetime = new AbortController();
  private readonly requestPrefix: string;
  private sequence = 0;
  private closed = false;

  constructor(
    readonly scope: MessageScope,
    readonly address: SessionAddress | null,
    private readonly send: (json: string) => void,
  ) {
    if ((scope === "session") !== (address !== null)) {
      throw new Error("A session bus requires an address and a host bus cannot have one.");
    }
    if (address !== null && (address.slot.length === 0 || address.incarnation.length === 0)) {
      throw new Error("A session bus requires a complete address.");
    }
    this.requestPrefix = `${scope[0]}-${PAGE_EPOCH}-${++busSequence}`;
  }

  get isClosed(): boolean {
    return this.closed;
  }

  get signal(): AbortSignal {
    return this.lifetime.signal;
  }

  feature(name: string): MessageFeature {
    if (name.length === 0) {
      throw new Error("A message feature needs a name.");
    }
    return new MessageFeature(this, name);
  }

  receive(envelope: MessageEnvelope): void {
    if (
      this.closed ||
      envelope.scope !== this.scope ||
      !addressEquals(envelope.session, this.address)
    ) {
      return;
    }

    if (envelope.kind === "response") {
      this.receiveResponse(envelope);
    } else if (envelope.kind === "cancel") {
      if (envelope.requestId !== null) {
        const incoming = this.incoming.get(envelope.requestId);
        if (incoming?.feature === envelope.feature && incoming.name === envelope.name) {
          incoming.cancellation.abort();
        }
      }
    } else if (envelope.kind === "event") {
      this.receiveEvent(envelope);
    } else {
      this.receiveRequest(envelope);
    }
  }

  close(reason: string): void {
    if (this.closed) {
      return;
    }
    this.closed = true;
    this.lifetime.abort();
    for (const request of this.pending.values()) {
      request.cleanup();
      request.reject(new Error(reason));
    }
    this.pending.clear();
    for (const request of this.incoming.values()) {
      request.cancellation.abort();
    }
    this.incoming.clear();
    this.events.clear();
    this.requests.clear();
  }

  linkDropped(reason: string): void {
    for (const request of this.pending.values()) {
      request.cleanup();
      request.reject(new Error(reason));
    }
    this.pending.clear();
    for (const request of this.incoming.values()) {
      request.cancellation.abort();
    }
    this.incoming.clear();
  }

  on<T>(feature: string, name: string, handler: EventHandler<T>): () => void {
    this.assertOpen();
    this.assertRoute(feature, name);
    const key = keyOf(feature, name);
    let handlers = this.events.get(key);
    if (handlers === undefined) {
      handlers = new Set();
      this.events.set(key, handlers);
    }
    handlers.add(handler as EventHandler<unknown>);
    return () => {
      handlers?.delete(handler as EventHandler<unknown>);
      if (handlers?.size === 0) {
        this.events.delete(key);
      }
    };
  }

  handle<TRequest, TResponse>(
    feature: string,
    name: string,
    handler: RequestHandler<TRequest, TResponse>,
    concurrent: boolean,
  ): () => void {
    this.assertOpen();
    this.assertRoute(feature, name);
    const key = keyOf(feature, name);
    if (this.requests.has(key)) {
      throw new Error(`A handler for ${feature}.${name} is already registered.`);
    }
    const registered: RegisteredRequest = {
      run: handler as RequestHandler<unknown, unknown>,
      concurrent,
    };
    this.requests.set(key, registered);
    return () => {
      if (this.requests.get(key) === registered) {
        this.requests.delete(key);
      }
    };
  }

  afterPriorMessages(feature: string, work: () => void | Promise<void>): void {
    this.assertOpen();
    if (feature.length === 0) {
      throw new Error("A message feature needs a name.");
    }
    this.enqueue(feature, false, async () => {
      if (!this.closed) {
        await work();
      }
    });
  }

  publish<T>(feature: string, name: string, payload: T): void {
    this.assertOpen();
    this.assertRoute(feature, name);
    this.sendEnvelope({
      scope: this.scope,
      session: this.address,
      kind: "event",
      requestId: null,
      feature,
      name,
      payload,
      error: null,
    });
  }

  request<TRequest, TResponse>(
    feature: string,
    name: string,
    payload: TRequest,
    signal?: AbortSignal,
  ): Promise<TResponse> {
    this.assertOpen();
    this.assertRoute(feature, name);
    const requestId = `${this.requestPrefix}-${++this.sequence}`;
    return new Promise<TResponse>((resolve, reject) => {
      const abort = (): void => {
        if (!this.pending.delete(requestId)) {
          return;
        }
        try {
          this.sendEnvelope({
            scope: this.scope,
            session: this.address,
            kind: "cancel",
            requestId,
            feature,
            name,
            payload: null,
            error: null,
          });
        } catch {
          // The local request is already settled; a disconnected transport cannot receive cancellation.
        } finally {
          reject(
            signal?.reason instanceof Error
              ? signal.reason
              : new Error("The request was cancelled."),
          );
        }
      };
      if (signal?.aborted === true) {
        reject(
          signal.reason instanceof Error ? signal.reason : new Error("The request was cancelled."),
        );
        return;
      }
      this.pending.set(requestId, {
        feature,
        name,
        resolve: (response) => {
          signal?.removeEventListener("abort", abort);
          resolve(response as TResponse);
        },
        reject: (error) => {
          signal?.removeEventListener("abort", abort);
          reject(error);
        },
        cleanup: () => signal?.removeEventListener("abort", abort),
      });
      signal?.addEventListener("abort", abort, { once: true });
      try {
        this.sendEnvelope({
          scope: this.scope,
          session: this.address,
          kind: "request",
          requestId,
          feature,
          name,
          payload,
          error: null,
        });
      } catch (error) {
        this.pending.get(requestId)?.cleanup();
        this.pending.delete(requestId);
        reject(error);
      }
    });
  }

  private receiveResponse(envelope: MessageEnvelope): void {
    if (envelope.requestId === null) {
      return;
    }
    const pending = this.pending.get(envelope.requestId);
    if (
      pending === undefined ||
      pending.feature !== envelope.feature ||
      pending.name !== envelope.name
    ) {
      return;
    }
    this.pending.delete(envelope.requestId);
    pending.cleanup();
    if (envelope.error === null) {
      pending.resolve(envelope.payload);
    } else {
      pending.reject(new Error(envelope.error));
    }
  }

  private receiveEvent(envelope: MessageEnvelope): void {
    const key = keyOf(envelope.feature, envelope.name);
    const handlers = [...(this.events.get(key) ?? [])];
    if (handlers.length === 0) {
      return;
    }
    this.enqueue(envelope.feature, false, async () => {
      if (this.closed) {
        return;
      }
      for (const handler of handlers) {
        if (this.closed) {
          return;
        }
        if (this.events.get(key)?.has(handler) === true) {
          try {
            await handler(envelope.payload, this.lifetime.signal);
          } catch (error) {
            console.error(`message handler ${envelope.feature}.${envelope.name} failed`, error);
          }
        }
      }
    });
  }

  private receiveRequest(envelope: MessageEnvelope): void {
    const requestId = envelope.requestId;
    if (requestId === null) {
      return;
    }
    const handler = this.requests.get(keyOf(envelope.feature, envelope.name));
    if (handler === undefined) {
      this.respond(
        envelope,
        null,
        `No handler is registered for ${envelope.feature}.${envelope.name}.`,
      );
      return;
    }
    if (this.incoming.has(requestId)) {
      return;
    }
    const cancellation = new AbortController();
    this.incoming.set(requestId, {
      feature: envelope.feature,
      name: envelope.name,
      cancellation,
    });
    this.enqueue(envelope.feature, handler.concurrent, async () => {
      try {
        if (this.closed || cancellation.signal.aborted) {
          return;
        }
        if (this.requests.get(keyOf(envelope.feature, envelope.name)) !== handler) {
          this.respond(
            envelope,
            null,
            `No handler is registered for ${envelope.feature}.${envelope.name}.`,
          );
          return;
        }
        const response = await handler.run(envelope.payload, cancellation.signal);
        if (!cancellation.signal.aborted) {
          this.respond(envelope, response, null);
        }
      } catch (error) {
        if (!cancellation.signal.aborted) {
          this.respond(envelope, null, error instanceof Error ? error.message : String(error));
        }
      } finally {
        if (this.incoming.get(requestId)?.cancellation === cancellation) {
          this.incoming.delete(requestId);
        }
      }
    });
  }

  private enqueue(feature: string, concurrent: boolean, work: () => Promise<void>): void {
    if (concurrent) {
      void work().catch((error: unknown) =>
        console.error(`message handler ${feature} failed`, error),
      );
      return;
    }
    const previous = this.lanes.get(feature) ?? Promise.resolve();
    const next = previous.catch(() => undefined).then(work);
    this.lanes.set(feature, next);
    void next
      .catch((error: unknown) => console.error(`message handler ${feature} failed`, error))
      .finally(() => {
        if (this.lanes.get(feature) === next) {
          this.lanes.delete(feature);
        }
      });
  }

  private respond(request: MessageEnvelope, payload: unknown, error: string | null): void {
    if (this.closed) {
      return;
    }
    try {
      this.sendEnvelope({
        scope: this.scope,
        session: this.address,
        kind: "response",
        requestId: request.requestId,
        feature: request.feature,
        name: request.name,
        payload,
        error,
      });
    } catch (sendError) {
      console.error(`response delivery for ${request.feature}.${request.name} failed`, sendError);
    }
  }

  private sendEnvelope(envelope: MessageEnvelope): void {
    this.send(JSON.stringify(envelope));
  }

  private assertOpen(): void {
    if (this.closed) {
      throw new Error("The message endpoint is closed.");
    }
  }

  private assertRoute(feature: string, name: string): void {
    if (feature.length === 0 || name.length === 0) {
      throw new Error("A message route needs a feature and name.");
    }
  }
}

export class MessageFeature {
  constructor(
    private readonly bus: MessageBus,
    private readonly name: string,
  ) {}

  on<T>(name: string, handler: EventHandler<T>): () => void {
    return this.bus.on(this.name, name, handler);
  }

  handle<TRequest, TResponse>(
    name: string,
    handler: RequestHandler<TRequest, TResponse>,
  ): () => void {
    return this.bus.handle(this.name, name, handler, false);
  }

  handleConcurrent<TRequest, TResponse>(
    name: string,
    handler: RequestHandler<TRequest, TResponse>,
  ): () => void {
    return this.bus.handle(this.name, name, handler, true);
  }

  afterPriorMessages(work: () => void | Promise<void>): void {
    this.bus.afterPriorMessages(this.name, work);
  }

  publish<T>(name: string, payload: T): void {
    this.bus.publish(this.name, name, payload);
  }

  request<TResponse, TRequest = Record<string, never>>(
    name: string,
    payload: TRequest,
    signal?: AbortSignal,
  ): Promise<TResponse> {
    return this.bus.request<TRequest, TResponse>(this.name, name, payload, signal);
  }
}
