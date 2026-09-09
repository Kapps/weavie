import type { ClientSession } from "../bridge";
import type { InlineDiffActions } from "./inline-diff";
import type { TabViewState } from "./nav-history";
import type { Placement } from "./session-store";
import type { EditorSessionEntry } from "./session-types";

export interface TabPresenter {
  readonly signal: AbortSignal;
  readonly text: boolean;
  capture(): TabViewState;
  restore(placement: Placement, signal: AbortSignal): Promise<void>;
  focus(): void;
  actions(): Partial<InlineDiffActions> | undefined;
}

/** An open tab owns its mounted view; reopening a resource creates a new owner. */
export class TabOwner {
  private readonly lifetime = new AbortController();
  private readonly waiters = new Set<(value: TabPresenter | Error) => void>();
  private failure: Error | undefined;
  private mounted: TabPresenter | undefined;

  constructor(
    readonly session: ClientSession,
    public entry: EditorSessionEntry,
  ) {}

  get signal(): AbortSignal {
    return this.lifetime.signal;
  }
  get presentation(): TabPresenter | undefined {
    return this.mounted;
  }

  assertLive(): void {
    if (this.signal.aborted || this.session.signal.aborted)
      throw new Error("The tab for this command has closed.");
  }

  mount(view: Omit<TabPresenter, "signal">): () => void {
    this.assertLive();
    if (this.mounted !== undefined) throw new Error("The tab already has a presenter.");
    const lifetime = new AbortController();
    const presentation = {
      ...view,
      signal: AbortSignal.any([this.signal, this.session.signal, lifetime.signal]),
    };
    this.failure = undefined;
    this.mounted = presentation;
    for (const ready of this.waiters) ready(presentation);
    return () => {
      lifetime.abort();
      if (this.mounted === presentation) this.mounted = undefined;
    };
  }

  failed(error: Error): void {
    this.failure = error;
    for (const ready of this.waiters) ready(error);
  }

  wait(signal: AbortSignal): Promise<TabPresenter> {
    const validity = AbortSignal.any([this.signal, this.session.signal, signal]);
    validity.throwIfAborted();
    if (this.failure !== undefined) return Promise.reject(this.failure);
    if (this.mounted !== undefined) return Promise.resolve(this.mounted);
    return new Promise((resolve, reject) => {
      const finish = (value: TabPresenter | Error): void => {
        this.waiters.delete(finish);
        validity.removeEventListener("abort", cancel);
        if (value instanceof Error) reject(value);
        else resolve(value);
      };
      const cancel = (): void => finish(new DOMException("Tab activation cancelled", "AbortError"));
      this.waiters.add(finish);
      validity.addEventListener("abort", cancel, { once: true });
    });
  }

  dispose(): void {
    this.lifetime.abort();
  }
}

export function focusTabContent(element: HTMLElement): void {
  if (element.shadowRoot?.activeElement == null && !element.contains(document.activeElement))
    element.focus();
}
