import { type Accessor, createSignal, type Setter } from "solid-js";
import type { EditorSession } from "../editor/session-types";
import type { WeavieLspConfig } from "../lsp/types";
import type { MessageBus } from "./message-bus";

type EditorSessionWire = {
  active: string | null;
  open: (Omit<EditorSession["open"][number], "kind"> & {
    kind?: EditorSession["open"][number]["kind"] | null;
  })[];
};

export class SessionValue<T> {
  private readonly read: Accessor<T>;
  private readonly write: Setter<T>;
  private readonly listeners = new Set<(value: T) => void>();

  constructor(initial: T) {
    [this.read, this.write] = createSignal(initial);
  }

  get current(): T {
    return this.read();
  }

  set(value: T): void {
    this.write(() => value);
    for (const listener of this.listeners) {
      listener(value);
    }
  }

  subscribe(listener: (value: T) => void): () => void {
    this.listeners.add(listener);
    listener(this.current);
    return () => this.listeners.delete(listener);
  }
}

export class ClientSessionState {
  readonly editor = new SessionValue<EditorSession | null>(null);
  readonly lsp = new SessionValue<WeavieLspConfig | null>(null);

  constructor(bus: MessageBus) {
    bus.feature("editor").on<{ session: EditorSessionWire }>("restore", ({ session }) =>
      this.editor.set({
        active: session.active,
        open: session.open.map((entry) => {
          const { kind, ...rest } = entry;
          return kind == null ? rest : { ...rest, kind };
        }),
      }),
    );
    bus.feature("lsp").on<WeavieLspConfig>("config", (config) => this.lsp.set(config));
  }
}
