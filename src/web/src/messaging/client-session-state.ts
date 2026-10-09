import { type Accessor, createSignal, type Setter } from "solid-js";
import type { EditorSession } from "../editor/session-types";
import type { WeavieLspConfig } from "../lsp/types";
import type { MessageBus } from "./message-bus";

type EditorSessionWire = Omit<EditorSession, "open"> & {
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

/** The host's tabs and the editor revision they reflect. */
export interface RestoredEditor {
  session: EditorSession;
  revision: number;
}

export class ClientSessionState {
  readonly editor = new SessionValue<RestoredEditor | null>(null);
  readonly lsp = new SessionValue<WeavieLspConfig | null>(null);

  constructor(bus: MessageBus) {
    bus
      .feature("editor")
      .on<{ session: EditorSessionWire; revision: number }>("restore", ({ session, revision }) =>
        this.editor.set({
          session: {
            ...session,
            open: session.open.map((entry) => {
              const { kind, ...rest } = entry;
              return kind == null ? rest : { ...rest, kind };
            }),
          },
          revision,
        }),
      );
    bus.feature("lsp").on<WeavieLspConfig>("config", (config) => this.lsp.set(config));
  }
}
