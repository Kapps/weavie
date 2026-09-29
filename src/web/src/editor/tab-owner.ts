import { type Accessor, batch, createSignal, type Setter, untrack } from "solid-js";
import type { ClientSession } from "../bridge";
import type { InlineDiffActions } from "./inline-diff";
import type { TabViewState } from "./nav-history";
import type { Placement } from "./session-store";
import type { EditorSessionEntry, EditorTab, EditorViewState } from "./session-types";

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
  private readonly readPresentation: Accessor<TabPresenter | undefined>;
  private readonly writePresentation: Setter<TabPresenter | undefined>;

  constructor(
    readonly session: ClientSession,
    public entry: EditorTab,
    private savedViewState: EditorViewState | null,
  ) {
    [this.readPresentation, this.writePresentation] = createSignal<TabPresenter>();
  }

  get viewState(): EditorViewState | null {
    return this.savedViewState;
  }

  saveViewState(state: EditorViewState | null): void {
    this.savedViewState = state;
  }

  snapshot(): EditorSessionEntry {
    return { ...this.entry, viewState: this.savedViewState };
  }

  get signal(): AbortSignal {
    return this.lifetime.signal;
  }
  get presentation(): TabPresenter | undefined {
    return this.readPresentation();
  }

  assertLive(): void {
    if (this.signal.aborted || this.session.signal.aborted)
      throw new Error("The tab for this command has closed.");
  }

  mount(view: Omit<TabPresenter, "signal">): () => void {
    this.assertLive();
    if (untrack(this.readPresentation) !== undefined)
      throw new Error("The tab already has a presenter.");
    const lifetime = new AbortController();
    const presentation = {
      ...view,
      signal: AbortSignal.any([this.signal, this.session.signal, lifetime.signal]),
    };
    this.failure = undefined;
    batch(() => {
      this.writePresentation(presentation);
      for (const ready of this.waiters) ready(presentation);
    });
    return () => {
      batch(() => {
        lifetime.abort();
        if (untrack(this.readPresentation) === presentation) this.writePresentation(undefined);
      });
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
    const presentation = untrack(this.readPresentation);
    if (presentation !== undefined) return Promise.resolve(presentation);
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
    batch(() => {
      this.lifetime.abort();
      this.writePresentation(undefined);
    });
  }
}

export function focusTabContent(element: HTMLElement): void {
  if (element.shadowRoot?.activeElement == null && !element.contains(document.activeElement))
    element.focus();
}
