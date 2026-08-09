import { setContext } from "../commands/context";

const openedListeners = new Set<() => void>();
interface ModalRequest {
  activate: () => void;
}

const pending: ModalRequest[] = [];
let activeModal: ModalRequest | null = null;

const activateNext = (): void => {
  if (activeModal !== null) {
    return;
  }
  activeModal = pending.shift() ?? null;
  if (activeModal === null) {
    setContext("modalOpen", false);
    return;
  }
  setContext("modalOpen", true);
  for (const listener of [...openedListeners]) {
    listener();
  }
  activeModal.activate();
};

/** Queues a modal for the app's single slot and returns a cancellation/release function. */
export function requestModal(activate: () => void): () => void {
  const request = { activate };
  pending.push(request);
  activateNext();
  let registered = true;
  return () => {
    if (!registered) {
      return;
    }
    registered = false;
    if (activeModal === request) {
      activeModal = null;
      activateNext();
      return;
    }
    const index = pending.indexOf(request);
    if (index >= 0) {
      pending.splice(index, 1);
    }
  };
}

/** Whether a modal currently owns the app's modal slot. */
export function modalActive(): boolean {
  return activeModal !== null;
}

/** Subscribes transient overlays that must dismiss when a modal opens. */
export function onModalOpened(listener: () => void): () => void {
  openedListeners.add(listener);
  return () => openedListeners.delete(listener);
}
